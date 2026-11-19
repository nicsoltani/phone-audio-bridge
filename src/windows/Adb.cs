using System.Diagnostics;
using System.Text;

namespace PhoneAudioBridge;

/// <summary>Thin wrapper around adb.exe (Android platform-tools).</summary>
static class Adb
{
    public record Device(string Serial, string State);

    public static string? Locate(string? configured)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configured)) candidates.Add(configured);
        var baseDir = AppContext.BaseDirectory;
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? baseDir;
        foreach (var dir in new[] { exeDir, baseDir }.Distinct())
        {
            candidates.Add(Path.Combine(dir, "adb.exe"));
            candidates.Add(Path.Combine(dir, "platform-tools", "adb.exe"));
        }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (!string.IsNullOrWhiteSpace(dir)) candidates.Add(Path.Combine(dir.Trim(), "adb.exe"));
        }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        candidates.Add(Path.Combine(local, "Android", "Sdk", "platform-tools", "adb.exe"));
        candidates.Add(@"C:\platform-tools\adb.exe");
        foreach (var c in candidates)
        {
            try { if (File.Exists(c)) return c; } catch { }
        }
        return null;
    }

    /// <summary>Runs adb and returns (exit code, combined output). Exit code -1 = timed out.</summary>
    public static (int Code, string Output) Run(string adb, string args, int timeoutMs = 15000)
    {
        var psi = new ProcessStartInfo(adb, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        var sb = new StringBuilder();
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(true); } catch { }
            lock (sb) return (-1, sb.ToString());
        }
        // Give the async readers a moment. (Not the parameterless WaitForExit: when adb has
        // just spawned its background server, that server keeps the pipes open forever.)
        Thread.Sleep(120);
        lock (sb) return (p.ExitCode, sb.ToString());
    }

    public static List<Device> Devices(string adb)
    {
        var list = new List<Device>();
        var (_, output) = Run(adb, "devices", 20000);
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("List of") || line.StartsWith("*")) continue;
            var parts = line.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2) list.Add(new Device(parts[0], parts[1]));
        }
        return list;
    }
}
