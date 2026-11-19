using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace PhoneAudioBridge;

enum BridgeState { Idle, Waiting, Connected }

/// <summary>
/// Keeps a link to the phone app alive over USB (adb) and moves audio both ways:
///   PC playback device  --WASAPI loopback-->  phone speaker
///   phone microphone    -->  virtual cable (VB-CABLE) that Windows apps use as a microphone
///
/// Protocol v1 (little-endian), mirrored in the Android AudioService:
///   PC    -> phone  hello: "PAB1" | u32 speakerSampleRate | u8 speakerChannels | u8 flags
///   phone -> PC     hello: "PAB1" | u32 micSampleRate     | u8 micChannels     | u8 mode
///   frames both ways: u8 type | u16 length | payload   (1 = PCM s16le, 2 = keep-alive)
/// </summary>
sealed class Bridge : IDisposable
{
    public const int Port = 38127;
    public const string SocketName = "phoneaudiobridge";
    public const string Package = "com.nicsoltani.phoneaudiobridge";
    const byte TypeAudio = 1, TypePing = 2;
    const int MaxChunk = 16384;
    const int MicBufferMs = 120;
    static readonly byte[] Magic = "PAB1"u8.ToArray();

    readonly Settings _settings;
    readonly AutoResetEvent _wake = new(false);
    readonly Thread _thread;
    volatile bool _stop;
    volatile TcpClient? _client;
    volatile bool _launchRequested;
    string? _lastSerial;

    public event Action<BridgeState, string>? StatusChanged;
    public string? CurrentSerial { get; private set; }
    public string? CurrentAdb { get; private set; }

    public Bridge(Settings settings)
    {
        _settings = settings;
        _thread = new Thread(Run) { IsBackground = true, Name = "bridge" };
    }

    public void Start() => _thread.Start();

    /// <summary>Drops the current session (if any) and reconnects with the current settings.</summary>
    public void Restart(bool launchPhoneApp = false)
    {
        if (launchPhoneApp) _launchRequested = true;
        try { _client?.Close(); } catch { }
        _wake.Set();
    }

    public void Dispose()
    {
        _stop = true;
        try { _client?.Close(); } catch { }
        _wake.Set();
        _thread.Join(3000);
    }

    void SetStatus(BridgeState state, string text)
    {
        StatusChanged?.Invoke(state, text);
    }

    void Run()
    {
        string lastError = "";
        while (!_stop)
        {
            try
            {
                Cycle();
                lastError = "";
            }
            catch (Exception ex)
            {
                if (ex.Message != lastError) Log.Write("Error: " + ex);
                lastError = ex.Message;
                SetStatus(BridgeState.Waiting, "Error: " + ex.Message);
            }
            if (!_stop) _wake.WaitOne(2500);
        }
    }

    void Cycle()
    {
        var adb = Adb.Locate(_settings.AdbPath);
        CurrentAdb = adb;
        if (adb == null)
        {
            CurrentSerial = null;
            SetStatus(BridgeState.Idle, "adb.exe not found - see README (platform-tools)");
            return;
        }

        var devices = Adb.Devices(adb);
        var ready = devices.FirstOrDefault(d => d.State == "device");
        if (ready == null)
        {
            CurrentSerial = null;
            _lastSerial = null;
            if (devices.Any(d => d.State == "unauthorized"))
                SetStatus(BridgeState.Waiting, "Unlock the phone and tap Allow on the USB debugging prompt");
            else
                SetStatus(BridgeState.Idle, "Phone not detected - plug in USB and enable USB debugging");
            return;
        }

        var serial = ready.Serial;
        CurrentSerial = serial;
        var (code, output) = Adb.Run(adb, $"-s {serial} forward tcp:{Port} localabstract:{SocketName}");
        if (code != 0) throw new InvalidOperationException("adb forward failed: " + output.Trim());

        bool newDevice = serial != _lastSerial;
        _lastSerial = serial;
        if ((newDevice && _settings.AutoLaunchPhoneApp) || _launchRequested)
        {
            _launchRequested = false;
            Log.Write("Starting the phone app on " + serial);
            Adb.Run(adb, $"-s {serial} shell am start -n {Package}/.MainActivity --ez autostart true");
            Thread.Sleep(1500);
        }

        using var client = new TcpClient { NoDelay = true };
        client.Connect(IPAddress.Loopback, Port);
        _client = client;
        try
        {
            RunSession(client);
        }
        finally
        {
            _client = null;
        }
    }

    static void ReadExactly(NetworkStream s, byte[] buf, int count)
    {
        int off = 0;
        while (off < count)
        {
            int n = s.Read(buf, off, count - off);
            if (n <= 0) throw new EndOfStreamException();
            off += n;
        }
    }

    void RunSession(TcpClient client)
    {
        var stream = client.GetStream();
        stream.ReadTimeout = 5000;
        stream.WriteTimeout = 5000;

        using var enumerator = new MMDeviceEnumerator();
        var notes = new List<string>();

        // Where the phone microphone is played into (the virtual cable).
        MMDevice? micCable = null;
        if (_settings.MicEnabled)
        {
            micCable = AudioDevices.ResolveMicCable(enumerator, _settings.MicCableDeviceId);
            if (micCable == null) notes.Add("VB-CABLE not found, microphone off");
        }

        // Which PC playback device is sent to the phone.
        MMDevice? source = null;
        if (_settings.SpeakerEnabled)
        {
            source = AudioDevices.ResolveSource(enumerator, _settings.SourceDeviceId);
            if (source == null) notes.Add("no playback device, speaker off");
            else if (micCable != null && source.ID == micCable.ID)
            {
                // Sending the cable to the phone would feed the microphone back into the speaker.
                source = null;
                notes.Add("PC sound source is the mic cable - choose another output device");
            }
        }

        WasapiLoopbackCapture? capture = null;
        WasapiOut? micOut = null;
        Thread? sender = null;
        using var cts = new CancellationTokenSource();
        using var queue = new BlockingCollection<byte[]>(8);
        try
        {
            int rate = 48000, channels = 2;
            WaveFormat? srcFormat = null;
            if (source != null)
            {
                capture = new WasapiLoopbackCapture(source);
                srcFormat = capture.WaveFormat;
                PcmConverter.EnsureSupported(srcFormat);
                rate = srcFormat.SampleRate;
                channels = Math.Min(2, srcFormat.Channels);
            }

            // ---- handshake
            var hello = new byte[10];
            Magic.CopyTo(hello, 0);
            BinaryPrimitives.WriteInt32LittleEndian(hello.AsSpan(4), rate);
            hello[8] = (byte)channels;
            hello[9] = (byte)((source != null ? 1 : 0) | (micCable != null ? 2 : 0));
            stream.Write(hello, 0, hello.Length);

            var reply = new byte[10];
            try
            {
                ReadExactly(stream, reply, reply.Length);
            }
            catch (Exception ex) when (ex is IOException or EndOfStreamException)
            {
                SetStatus(BridgeState.Waiting, "Phone connected - open Phone Audio Bridge on the phone and tap Start");
                return;
            }
            if (!reply.AsSpan(0, 4).SequenceEqual(Magic)) throw new InvalidDataException("Unexpected reply from the phone app");
            int micRate = BinaryPrimitives.ReadInt32LittleEndian(reply.AsSpan(4));
            int micChannels = reply[8];
            bool musicMode = reply[9] == 1;
            if (micRate < 8000 || micRate > 192000 || micChannels < 1 || micChannels > 2)
                throw new InvalidDataException("Unsupported microphone format from the phone app");

            // ---- phone microphone -> virtual cable
            BufferedWaveProvider? micBuffer = null;
            if (micCable != null)
            {
                micBuffer = new BufferedWaveProvider(new WaveFormat(micRate, 16, micChannels))
                {
                    BufferDuration = TimeSpan.FromMilliseconds(MicBufferMs),
                    DiscardOnBufferOverflow = true,   // bounded latency: drop rather than lag
                    ReadFully = true,                 // play silence when the phone sends nothing
                };
                ISampleProvider samples = micBuffer.ToSampleProvider();
                if (micChannels == 1) samples = new MonoToStereoSampleProvider(samples);
                micOut = new WasapiOut(micCable, AudioClientShareMode.Shared, true, 40);
                micOut.Init(samples.ToWaveProvider());
                micOut.PlaybackStopped += (_, e) =>
                {
                    if (e.Exception != null) Log.Write("Microphone cable stopped: " + e.Exception.Message);
                    try { client.Close(); } catch { }
                };
                micOut.Play();
            }

            // ---- PC sound -> phone speaker
            if (capture != null && srcFormat != null)
            {
                var fmt = srcFormat;
                int outChannels = channels;
                capture.DataAvailable += (_, e) =>
                {
                    if (e.BytesRecorded <= 0) return;
                    var pcm = PcmConverter.ToPcm16(e.Buffer, e.BytesRecorded, fmt, outChannels);
                    queue.TryAdd(pcm);   // full queue = phone is not keeping up: drop
                };
                capture.RecordingStopped += (_, e) =>
                {
                    if (e.Exception != null) Log.Write("PC sound capture stopped: " + e.Exception.Message);
                    try { cts.Cancel(); } catch { }
                    try { client.Close(); } catch { }
                };
                capture.StartRecording();
            }
            sender = new Thread(() => SendLoop(stream, queue, cts.Token, client)) { IsBackground = true, Name = "bridge-send" };
            sender.Start();

            var parts = new List<string>();
            if (source != null) parts.Add("speaker: " + source.FriendlyName);
            if (micCable != null) parts.Add("mic: " + micCable.FriendlyName);
            parts.Add(musicMode ? "music mode" : "call mode");
            parts.AddRange(notes);
            var text = "Connected - " + string.Join(", ", parts);
            Log.Write(text);
            SetStatus(BridgeState.Connected, text);

            // ---- receive loop (this thread)
            var header = new byte[3];
            var payload = new byte[65536];
            bool followDefault = source != null && string.IsNullOrEmpty(_settings.SourceDeviceId);
            var lastCheck = System.Diagnostics.Stopwatch.StartNew();
            while (!_stop && !cts.IsCancellationRequested)
            {
                if (followDefault && lastCheck.ElapsedMilliseconds > 2000)
                {
                    lastCheck.Restart();
                    var current = AudioDevices.ResolveSource(enumerator, null);
                    if (current != null && current.ID != source!.ID)
                    {
                        Log.Write("Windows default output changed - reconnecting");
                        _wake.Set();   // reconnect immediately
                        return;
                    }
                }
                ReadExactly(stream, header, 3);
                int len = header[1] | (header[2] << 8);
                if (len > 0) ReadExactly(stream, payload, len);
                if (header[0] == TypeAudio && micBuffer != null && len > 0)
                    micBuffer.AddSamples(payload, 0, len);
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or SocketException or ObjectDisposedException)
        {
            Log.Write("Link closed: " + ex.Message);
            SetStatus(BridgeState.Waiting, "Reconnecting...");
        }
        finally
        {
            try { cts.Cancel(); } catch { }
            try { client.Close(); } catch { }
            try { capture?.StopRecording(); } catch { }
            try { capture?.Dispose(); } catch { }
            try { micOut?.Stop(); } catch { }
            try { micOut?.Dispose(); } catch { }
            try { sender?.Join(2000); } catch { }
        }
    }

    static void SendLoop(NetworkStream stream, BlockingCollection<byte[]> queue, CancellationToken token, TcpClient client)
    {
        var frame = new byte[3 + MaxChunk];
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (queue.TryTake(out var pcm, 1000, token))
                {
                    for (int off = 0; off < pcm.Length; off += MaxChunk)
                    {
                        int n = Math.Min(MaxChunk, pcm.Length - off);
                        frame[0] = TypeAudio;
                        frame[1] = (byte)(n & 0xFF);
                        frame[2] = (byte)(n >> 8);
                        Buffer.BlockCopy(pcm, off, frame, 3, n);
                        stream.Write(frame, 0, 3 + n);
                    }
                }
                else
                {
                    // Nothing is playing on the PC: keep-alive so the phone knows we are here.
                    frame[0] = TypePing; frame[1] = 0; frame[2] = 0;
                    stream.Write(frame, 0, 3);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Write("Send stopped: " + ex.Message);
            try { client.Close(); } catch { }
        }
    }
}

static class AudioDevices
{
    public static List<MMDevice> PlaybackDevices(MMDeviceEnumerator enumerator) =>
        enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();

    public static bool LooksLikeVirtualCable(MMDevice d) =>
        d.FriendlyName.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase);

    static MMDevice? ById(MMDeviceEnumerator enumerator, string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        return PlaybackDevices(enumerator).FirstOrDefault(d => d.ID == id);
    }

    /// <summary>The playback side of the virtual cable: configured device, else VB-CABLE ("CABLE Input").</summary>
    public static MMDevice? ResolveMicCable(MMDeviceEnumerator enumerator, string? configuredId) =>
        ById(enumerator, configuredId) ?? PlaybackDevices(enumerator).FirstOrDefault(LooksLikeVirtualCable);

    /// <summary>The playback device to send to the phone: configured device, else the Windows default.</summary>
    public static MMDevice? ResolveSource(MMDeviceEnumerator enumerator, string? configuredId)
    {
        var chosen = ById(enumerator, configuredId);
        if (chosen != null) return chosen;
        try
        {
            return enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                : null;
        }
        catch { return null; }
    }
}

static class PcmConverter
{
    static bool IsFloat(WaveFormat f) => f.Encoding == WaveFormatEncoding.IeeeFloat && f.BitsPerSample == 32;
    static bool IsPcm16(WaveFormat f) => f.Encoding == WaveFormatEncoding.Pcm && f.BitsPerSample == 16;

    public static void EnsureSupported(WaveFormat f)
    {
        if (!IsFloat(f) && !IsPcm16(f))
            throw new NotSupportedException($"Unsupported PC sound format: {f.Encoding} {f.BitsPerSample}-bit");
    }

    /// <summary>Converts a captured buffer to interleaved signed 16-bit with 1 or 2 channels.</summary>
    public static byte[] ToPcm16(byte[] buffer, int bytes, WaveFormat fmt, int outChannels)
    {
        int srcChannels = fmt.Channels;
        int blockAlign = fmt.BlockAlign;
        int frames = bytes / blockAlign;
        var result = new byte[frames * outChannels * 2];
        bool isFloat = IsFloat(fmt);
        bool surround = srcChannels >= 6;   // 5.1 / 7.1: fold the centre channel into left and right
        var span = buffer.AsSpan(0, frames * blockAlign);
        int o = 0;
        for (int i = 0; i < frames; i++)
        {
            var f = span.Slice(i * blockAlign, blockAlign);
            float centre = surround ? Sample(f, 2, isFloat) * 0.707f : 0f;
            for (int c = 0; c < outChannels; c++)
            {
                if (!isFloat && !surround)
                {
                    // Already 16-bit: copy the sample untouched.
                    f.Slice(c * 2, 2).CopyTo(result.AsSpan(o));
                    o += 2;
                    continue;
                }
                float v = Sample(f, c, isFloat) + centre;
                if (v > 1f) v = 1f; else if (v < -1f) v = -1f;
                BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(o), (short)(v * 32767f));
                o += 2;
            }
        }
        return result;
    }

    static float Sample(ReadOnlySpan<byte> frame, int channel, bool isFloat) =>
        isFloat
            ? BinaryPrimitives.ReadSingleLittleEndian(frame.Slice(channel * 4))
            : BinaryPrimitives.ReadInt16LittleEndian(frame.Slice(channel * 2)) / 32768f;
}
