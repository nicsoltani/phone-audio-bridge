package com.nicsoltani.phoneaudiobridge

import android.Manifest
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.content.pm.ServiceInfo
import android.media.AudioAttributes
import android.media.AudioDeviceInfo
import android.media.AudioFormat
import android.media.AudioManager
import android.media.AudioRecord
import android.media.AudioTrack
import android.media.MediaRecorder
import android.media.audiofx.AcousticEchoCanceler
import android.media.audiofx.AutomaticGainControl
import android.media.audiofx.NoiseSuppressor
import android.net.LocalServerSocket
import android.net.LocalSocket
import android.net.LocalSocketAddress
import android.os.Build
import android.os.IBinder
import android.os.PowerManager
import android.util.Log
import java.io.BufferedInputStream
import java.io.EOFException
import java.io.InputStream
import java.io.OutputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder
import kotlin.concurrent.thread
import kotlin.math.max

/**
 * Foreground service that turns the phone into the PC's microphone and speaker.
 *
 * Transport: a Unix abstract socket ("phoneaudiobridge") reached from the PC with
 *   adb forward tcp:38127 localabstract:phoneaudiobridge
 * Only the adb daemon (uid shell/root) is allowed to connect, so nothing on the
 * network and no other app on the phone can listen to the microphone.
 *
 * Protocol v1 (little-endian):
 *   PC    -> phone  hello: "PAB1" | u32 speakerSampleRate | u8 speakerChannels | u8 flags
 *   phone -> PC     hello: "PAB1" | u32 micSampleRate     | u8 micChannels     | u8 mode (0 call, 1 music)
 *   then frames both ways:  u8 type | u16 length | payload
 *     type 1 = PCM signed 16-bit little-endian, type 2 = keep-alive
 */
class AudioService : Service() {

    companion object {
        const val TAG = "PhoneAudioBridge"
        const val SOCKET_NAME = "phoneaudiobridge"
        const val ACTION_STOP = "com.nicsoltani.phoneaudiobridge.STOP"
        const val ACTION_RESTART_SESSION = "com.nicsoltani.phoneaudiobridge.RESTART_SESSION"
        const val PREFS = "settings"
        const val PREF_MUSIC_MODE = "music_mode"

        private const val CHANNEL_ID = "bridge"
        private const val NOTIFICATION_ID = 1
        private const val MIC_RATE = 48000
        private const val MIC_CHUNK = 960            // 10 ms of 48 kHz mono 16-bit
        private const val SPEAKER_BUFFER_MS = 120
        private const val TYPE_AUDIO = 1
        private const val TYPE_PING = 2
        private const val UID_ROOT = 0
        private const val UID_SHELL = 2000
        private val MAGIC = byteArrayOf('P'.code.toByte(), 'A'.code.toByte(), 'B'.code.toByte(), '1'.code.toByte())

        @Volatile var running = false
        @Volatile var status = "Stopped"
        @Volatile var micMuted = false
    }

    @Volatile private var server: LocalServerSocket? = null
    @Volatile private var client: LocalSocket? = null
    private var serverThread: Thread? = null
    @Volatile private var alive = false   // this instance (running is the app-wide flag for the UI)
    private var wakeLock: PowerManager.WakeLock? = null

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_STOP -> {
                stopSelf()
                return START_NOT_STICKY
            }
            ACTION_RESTART_SESSION -> {
                closeClient()
                if (running) return START_NOT_STICKY
            }
        }
        if (!running) {
            if (!enterForeground()) {
                stopSelf()
                return START_NOT_STICKY
            }
            running = true
            alive = true
            val pm = getSystemService(Context.POWER_SERVICE) as PowerManager
            wakeLock = pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "$TAG:bridge").apply { acquire() }
            serverThread = thread(name = "pab-server") { serverLoop() }
        }
        return START_NOT_STICKY
    }

    override fun onDestroy() {
        alive = false
        running = false
        closeClient()
        try { server?.close() } catch (_: Exception) {}
        // accept() does not always wake when the server socket is closed; poke it.
        thread {
            try {
                LocalSocket().use { it.connect(LocalSocketAddress(SOCKET_NAME)) }
            } catch (_: Exception) {}
        }
        try { wakeLock?.release() } catch (_: Exception) {}
        status = "Stopped"
        super.onDestroy()
    }

    // ---------------------------------------------------------------- foreground

    private fun hasMicPermission() =
        checkSelfPermission(Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED

    private fun enterForeground(): Boolean {
        val nm = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        nm.createNotificationChannel(
            NotificationChannel(CHANNEL_ID, "Audio bridge", NotificationManager.IMPORTANCE_LOW)
        )
        val open = PendingIntent.getActivity(
            this, 0, Intent(this, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE
        )
        val stop = PendingIntent.getService(
            this, 1, Intent(this, AudioService::class.java).setAction(ACTION_STOP), PendingIntent.FLAG_IMMUTABLE
        )
        val notification = Notification.Builder(this, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_stat)
            .setContentTitle("Phone Audio Bridge")
            .setContentText("Phone is acting as the PC microphone and speaker")
            .setContentIntent(open)
            .setOngoing(true)
            .addAction(Notification.Action.Builder(null, "Stop", stop).build())
            .build()
        return try {
            if (Build.VERSION.SDK_INT >= 30) {
                var types = ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PLAYBACK
                if (hasMicPermission()) types = types or ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE
                startForeground(NOTIFICATION_ID, notification, types)
            } else {
                startForeground(NOTIFICATION_ID, notification)
            }
            true
        } catch (e: Exception) {
            Log.e(TAG, "Cannot start foreground service", e)
            status = "Could not start: ${e.message}"
            false
        }
    }

    // -------------------------------------------------------------------- server

    private fun closeClient() {
        val c = client ?: return
        try { c.shutdownInput() } catch (_: Exception) {}
        try { c.shutdownOutput() } catch (_: Exception) {}
        try { c.close() } catch (_: Exception) {}
    }

    private fun serverLoop() {
        try {
            val ss = bindServer()
            server = ss
            while (alive) {
                status = "Ready - waiting for the PC (USB)"
                val s = ss.accept()
                if (!alive) {
                    try { s.close() } catch (_: Exception) {}
                    break
                }
                client = s
                try {
                    runSession(s)
                } catch (e: Exception) {
                    Log.i(TAG, "Session ended: $e")
                } finally {
                    closeClient()
                    client = null
                }
            }
        } catch (e: Exception) {
            if (alive) {
                Log.e(TAG, "Server failed", e)
                status = "Error: ${e.message}"
            }
        } finally {
            try { server?.close() } catch (_: Exception) {}
        }
    }

    /** A just-stopped instance may still hold the socket name for a moment, so retry briefly. */
    private fun bindServer(): LocalServerSocket {
        var last: Exception? = null
        repeat(10) {
            try {
                return LocalServerSocket(SOCKET_NAME)
            } catch (e: Exception) {
                last = e
                Thread.sleep(300)
            }
        }
        throw last ?: IllegalStateException("Cannot open socket")
    }

    private fun readFully(input: InputStream, buf: ByteArray, len: Int) {
        var off = 0
        while (off < len) {
            val n = input.read(buf, off, len - off)
            if (n < 0) throw EOFException()
            off += n
        }
    }

    private fun runSession(s: LocalSocket) {
        val uid = s.peerCredentials.uid
        if (uid != UID_SHELL && uid != UID_ROOT) {
            Log.w(TAG, "Rejected connection from uid $uid (only adb is allowed)")
            return
        }
        s.soTimeout = 5000
        val input = BufferedInputStream(s.inputStream, 32768)
        val output = s.outputStream

        val hello = ByteArray(10)
        readFully(input, hello, hello.size)
        for (i in MAGIC.indices) if (hello[i] != MAGIC[i]) throw IllegalStateException("Bad handshake")
        val bb = ByteBuffer.wrap(hello).order(ByteOrder.LITTLE_ENDIAN)
        val rate = bb.getInt(4)
        val channels = hello[8].toInt()
        if (rate !in 8000..192000 || channels !in 1..2) throw IllegalStateException("Unsupported format $rate/$channels")

        val musicMode = getSharedPreferences(PREFS, Context.MODE_PRIVATE).getBoolean(PREF_MUSIC_MODE, false)

        val reply = ByteBuffer.allocate(10).order(ByteOrder.LITTLE_ENDIAN)
        reply.put(MAGIC).putInt(MIC_RATE).put(1).put(if (musicMode) 1 else 0)
        output.write(reply.array())
        output.flush()

        val am = getSystemService(Context.AUDIO_SERVICE) as AudioManager
        var track: AudioTrack? = null
        var micThread: Thread? = null
        val session = Session()
        try {
            routeAudio(am, musicMode)
            track = createTrack(rate, channels, musicMode)
            track.play()
            micThread = thread(name = "pab-mic") { micLoop(output, musicMode, session) }
            status = "Connected to PC - " + if (musicMode) "music mode" else "call mode (echo cancellation)"

            val frameSize = channels * 2
            val header = ByteArray(3)
            val payload = ByteArray(65536)
            while (alive && session.active) {
                readFully(input, header, 3)
                val type = header[0].toInt() and 0xFF
                val len = (header[1].toInt() and 0xFF) or ((header[2].toInt() and 0xFF) shl 8)
                if (len > 0) readFully(input, payload, len)
                if (type == TYPE_AUDIO && len >= frameSize) {
                    // Non-blocking: if the speaker buffer is full the excess is dropped, which
                    // keeps latency bounded instead of letting it build up in the USB pipe.
                    track.write(payload, 0, len - (len % frameSize), AudioTrack.WRITE_NON_BLOCKING)
                }
            }
        } finally {
            session.active = false
            closeClient()
            try { micThread?.join(1500) } catch (_: Exception) {}
            try { track?.pause(); track?.flush(); track?.release() } catch (_: Exception) {}
            unrouteAudio(am)
        }
    }

    private class Session {
        @Volatile var active = true
    }

    // --------------------------------------------------------------------- audio

    private fun routeAudio(am: AudioManager, musicMode: Boolean) {
        if (musicMode) {
            am.mode = AudioManager.MODE_NORMAL
            return
        }
        // Call mode: the phone behaves like a speakerphone so its echo canceller
        // removes the PC sound from the microphone signal.
        am.mode = AudioManager.MODE_IN_COMMUNICATION
        if (Build.VERSION.SDK_INT >= 31) {
            val speaker = am.availableCommunicationDevices
                .firstOrNull { it.type == AudioDeviceInfo.TYPE_BUILTIN_SPEAKER }
            if (speaker != null) am.setCommunicationDevice(speaker)
        } else {
            @Suppress("DEPRECATION")
            am.isSpeakerphoneOn = true
        }
    }

    private fun unrouteAudio(am: AudioManager) {
        try {
            if (Build.VERSION.SDK_INT >= 31) {
                am.clearCommunicationDevice()
            } else {
                @Suppress("DEPRECATION")
                am.isSpeakerphoneOn = false
            }
            am.mode = AudioManager.MODE_NORMAL
        } catch (_: Exception) {}
    }

    private fun createTrack(rate: Int, channels: Int, musicMode: Boolean): AudioTrack {
        val mask = if (channels == 1) AudioFormat.CHANNEL_OUT_MONO else AudioFormat.CHANNEL_OUT_STEREO
        val minBuf = AudioTrack.getMinBufferSize(rate, mask, AudioFormat.ENCODING_PCM_16BIT)
        val wanted = rate * channels * 2 * SPEAKER_BUFFER_MS / 1000
        val attrs = AudioAttributes.Builder()
            .setUsage(if (musicMode) AudioAttributes.USAGE_MEDIA else AudioAttributes.USAGE_VOICE_COMMUNICATION)
            .setContentType(if (musicMode) AudioAttributes.CONTENT_TYPE_MUSIC else AudioAttributes.CONTENT_TYPE_SPEECH)
            .build()
        return AudioTrack.Builder()
            .setAudioAttributes(attrs)
            .setAudioFormat(
                AudioFormat.Builder()
                    .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                    .setSampleRate(rate)
                    .setChannelMask(mask)
                    .build()
            )
            .setBufferSizeInBytes(max(minBuf, wanted))
            .setTransferMode(AudioTrack.MODE_STREAM)
            .setPerformanceMode(AudioTrack.PERFORMANCE_MODE_LOW_LATENCY)
            .build()
    }

    private fun micLoop(output: OutputStream, musicMode: Boolean, session: Session) {
        var rec: AudioRecord? = null
        var aec: AcousticEchoCanceler? = null
        var ns: NoiseSuppressor? = null
        var agc: AutomaticGainControl? = null
        val frame = ByteArray(3 + MIC_CHUNK)
        try {
            if (hasMicPermission()) {
                val minBuf = AudioRecord.getMinBufferSize(
                    MIC_RATE, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT
                )
                val source = if (musicMode) MediaRecorder.AudioSource.MIC
                else MediaRecorder.AudioSource.VOICE_COMMUNICATION
                @Suppress("MissingPermission")
                val r = AudioRecord(
                    source, MIC_RATE, AudioFormat.CHANNEL_IN_MONO,
                    AudioFormat.ENCODING_PCM_16BIT, max(minBuf, MIC_CHUNK * 8)
                )
                if (r.state == AudioRecord.STATE_INITIALIZED) {
                    rec = r
                    if (!musicMode) {
                        val id = r.audioSessionId
                        if (AcousticEchoCanceler.isAvailable()) aec = AcousticEchoCanceler.create(id)?.apply { enabled = true }
                        if (NoiseSuppressor.isAvailable()) ns = NoiseSuppressor.create(id)?.apply { enabled = true }
                        if (AutomaticGainControl.isAvailable()) agc = AutomaticGainControl.create(id)?.apply { enabled = true }
                    }
                    r.startRecording()
                } else {
                    r.release()
                    Log.w(TAG, "Microphone could not be opened")
                }
            } else {
                Log.w(TAG, "No microphone permission - speaker only")
            }

            while (alive && session.active) {
                val r = rec
                if (r == null) {
                    // No microphone: send keep-alives so the PC knows the link is alive.
                    frame[0] = TYPE_PING.toByte(); frame[1] = 0; frame[2] = 0
                    output.write(frame, 0, 3)
                    Thread.sleep(1000)
                    continue
                }
                val n = r.read(frame, 3, MIC_CHUNK)
                if (n < 0) throw IllegalStateException("AudioRecord error $n")
                if (n == 0) continue
                if (micMuted) java.util.Arrays.fill(frame, 3, 3 + n, 0)
                frame[0] = TYPE_AUDIO.toByte()
                frame[1] = (n and 0xFF).toByte()
                frame[2] = ((n shr 8) and 0xFF).toByte()
                output.write(frame, 0, 3 + n)
            }
        } catch (e: Exception) {
            Log.i(TAG, "Mic loop ended: $e")
        } finally {
            session.active = false
            closeClient()
            try { aec?.release() } catch (_: Exception) {}
            try { ns?.release() } catch (_: Exception) {}
            try { agc?.release() } catch (_: Exception) {}
            try { rec?.stop() } catch (_: Exception) {}
            try { rec?.release() } catch (_: Exception) {}
        }
    }
}
