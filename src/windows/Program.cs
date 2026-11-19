using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using NAudio.CoreAudioApi;

namespace PhoneAudioBridge;

static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, "PhoneAudioBridge.SingleInstance", out bool first);
        if (!first) return;   // already running in the tray
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext());
    }
}

sealed class TrayContext : ApplicationContext
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "PhoneAudioBridge";

    readonly Settings _settings = Settings.Load();
    readonly Bridge _bridge;
    readonly NotifyIcon _tray;
    readonly Control _ui = new();
    readonly Dictionary<BridgeState, Icon> _icons = new();
    string _status = "Starting...";

    public TrayContext()
    {
        _ = _ui.Handle;   // create the handle on the UI thread so BeginInvoke works
        _icons[BridgeState.Idle] = MakeIcon(Color.FromArgb(120, 120, 120));
        _icons[BridgeState.Waiting] = MakeIcon(Color.FromArgb(230, 150, 0));
        _icons[BridgeState.Connected] = MakeIcon(Color.FromArgb(30, 150, 70));

        var menu = new ContextMenuStrip();
        menu.Opening += (_, _) => BuildMenu(menu);
        _tray = new NotifyIcon
        {
            Icon = _icons[BridgeState.Idle],
            Text = "Phone Audio Bridge",
            ContextMenuStrip = menu,
            Visible = true,
        };
        BuildMenu(menu);

        Log.Write("Started");
        _bridge = new Bridge(_settings);
        _bridge.StatusChanged += (state, text) =>
        {
            try { _ui.BeginInvoke(() => ShowStatus(state, text)); } catch { }
        };
        _bridge.Start();
    }

    void ShowStatus(BridgeState state, string text)
    {
        if (text == _status) return;
        _status = text;
        _tray.Icon = _icons[state];
        var tip = "Phone Audio Bridge - " + text;
        _tray.Text = tip.Length > 120 ? tip[..120] : tip;
    }

    void BuildMenu(ContextMenuStrip menu)
    {
        menu.Items.Clear();
        menu.Items.Add(new ToolStripMenuItem(_status) { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(Check("Play PC sound on the phone", _settings.SpeakerEnabled, v => _settings.SpeakerEnabled = v));
        menu.Items.Add(Check("Use the phone as microphone", _settings.MicEnabled, v => _settings.MicEnabled = v));

        var sourceMenu = new ToolStripMenuItem("PC sound source");
        var cableMenu = new ToolStripMenuItem("Microphone cable");
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var devices = AudioDevices.PlaybackDevices(enumerator);
            sourceMenu.DropDownItems.Add(Radio("Windows default output", _settings.SourceDeviceId == null,
                () => _settings.SourceDeviceId = null));
            cableMenu.DropDownItems.Add(Radio("Automatic (VB-CABLE)", _settings.MicCableDeviceId == null,
                () => _settings.MicCableDeviceId = null));
            foreach (var d in devices)
            {
                string id = d.ID, name = d.FriendlyName;
                if (!AudioDevices.LooksLikeVirtualCable(d))
                    sourceMenu.DropDownItems.Add(Radio(name, _settings.SourceDeviceId == id, () => _settings.SourceDeviceId = id));
                cableMenu.DropDownItems.Add(Radio(name, _settings.MicCableDeviceId == id, () => _settings.MicCableDeviceId = id));
            }
        }
        catch (Exception ex)
        {
            Log.Write("Could not list audio devices: " + ex.Message);
        }
        menu.Items.Add(sourceMenu);
        menu.Items.Add(cableMenu);
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add("Reconnect and start the phone app", null, (_, _) => _bridge.Restart(launchPhoneApp: true));
        menu.Items.Add("Install or update the phone app...", null, (_, _) => InstallApk());
        menu.Items.Add("Choose adb.exe...", null, (_, _) => ChooseAdb());
        menu.Items.Add(Check("Start the phone app automatically", _settings.AutoLaunchPhoneApp,
            v => _settings.AutoLaunchPhoneApp = v, reconnect: false));
        menu.Items.Add(new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleAutoStart()) { Checked = AutoStartEnabled() });
        menu.Items.Add("Windows sound settings", null, (_, _) => Open("ms-settings:sound"));
        menu.Items.Add("Open log", null, (_, _) => { if (File.Exists(Log.FilePath)) Open(Log.FilePath); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Exit());
    }

    ToolStripMenuItem Check(string text, bool value, Action<bool> set, bool reconnect = true)
    {
        var item = new ToolStripMenuItem(text) { Checked = value };
        item.Click += (_, _) =>
        {
            set(!value);
            _settings.Save();
            if (reconnect) _bridge.Restart();
        };
        return item;
    }

    ToolStripMenuItem Radio(string text, bool selected, Action choose)
    {
        var item = new ToolStripMenuItem(text) { Checked = selected };
        item.Click += (_, _) =>
        {
            choose();
            _settings.Save();
            _bridge.Restart();
        };
        return item;
    }

    static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write("Could not open " + target + ": " + ex.Message); }
    }

    void ChooseAdb()
    {
        using var dlg = new OpenFileDialog { Title = "Locate adb.exe (Android platform-tools)", Filter = "adb.exe|adb.exe" };
        if (dlg.ShowDialog() != DialogResult.OK) return;
        _settings.AdbPath = dlg.FileName;
        _settings.Save();
        _bridge.Restart();
    }

    void InstallApk()
    {
        var adb = _bridge.CurrentAdb;
        var serial = _bridge.CurrentSerial;
        if (adb == null || serial == null)
        {
            MessageBox.Show("Connect the phone by USB with USB debugging enabled first.\n\nStatus: " + _status,
                "Phone Audio Bridge", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? AppContext.BaseDirectory;
        var apk = Path.Combine(exeDir, "PhoneAudioBridge.apk");
        if (!File.Exists(apk))
        {
            using var dlg = new OpenFileDialog { Title = "Select PhoneAudioBridge.apk", Filter = "Android app (*.apk)|*.apk" };
            if (dlg.ShowDialog() != DialogResult.OK) return;
            apk = dlg.FileName;
        }
        var path = apk;
        Task.Run(() =>
        {
            var (code, output) = Adb.Run(adb, $"-s {serial} install -r \"{path}\"", 120000);
            Log.Write($"adb install exit {code}: {output.Trim()}");
            bool ok = code == 0 && output.Contains("Success", StringComparison.OrdinalIgnoreCase);
            try
            {
                _ui.BeginInvoke(() =>
                {
                    MessageBox.Show(ok ? "The phone app was installed." : "Installation failed:\n\n" + output.Trim(),
                        "Phone Audio Bridge", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                    if (ok) _bridge.Restart(launchPhoneApp: true);
                });
            }
            catch { }
        });
    }

    static bool AutoStartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) != null;
    }

    static void ToggleAutoStart()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (key.GetValue(RunValue) != null) key.DeleteValue(RunValue, false);
            else key.SetValue(RunValue, "\"" + Environment.ProcessPath + "\"");
        }
        catch (Exception ex) { Log.Write("Could not change auto-start: " + ex.Message); }
    }

    void Exit()
    {
        _tray.Visible = false;
        _bridge.Dispose();
        Log.Write("Exited");
        ExitThread();
    }

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);

    /// <summary>Tray icon: a coloured disc (state) with a white microphone.</summary>
    static Icon MakeIcon(Color colour)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var fill = new SolidBrush(colour);
            g.FillEllipse(fill, 1, 1, 30, 30);
            using var white = new SolidBrush(Color.White);
            using var pen = new Pen(Color.White, 2f);
            using var capsule = new GraphicsPath();
            capsule.AddArc(12, 6, 8, 8, 180, 180);
            capsule.AddArc(12, 11, 8, 8, 0, 180);
            capsule.CloseFigure();
            g.FillPath(white, capsule);
            g.DrawArc(pen, 9, 9, 14, 13, 0, 180);
            g.DrawLine(pen, 16, 22, 16, 26);
            g.DrawLine(pen, 12, 26, 20, 26);
        }
        var handle = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }
}
