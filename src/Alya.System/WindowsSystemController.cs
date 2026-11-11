using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Alya.System;

/// <summary>Windows-реализация системных действий.</summary>
public sealed class WindowsSystemController : Alya.Core.ISystemController
{
    private const int SwRestore = 9;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern int GetWindowThreadProcessId(IntPtr handle, out int processId);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr handle, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);

    private delegate bool EnumProc(IntPtr handle, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumProc callback, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr handle, global::System.Text.StringBuilder text, int max);

    public string? ForegroundProcess()
    {
        var handle = GetForegroundWindow();
        if (handle == IntPtr.Zero) return null;
        GetWindowThreadProcessId(handle, out var pid);
        try
        {
            return Process.GetProcessById(pid).ProcessName.ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }

    public void MediaKey(byte key)
    {
        keybd_event(key, 0, 0, UIntPtr.Zero);
        keybd_event(key, 0, 2, UIntPtr.Zero);
    }

    public void MinimizeAll()
    {
        keybd_event(0x5B, 0, 0, UIntPtr.Zero);
        keybd_event(0x44, 0, 0, UIntPtr.Zero);
        keybd_event(0x44, 0, 2, UIntPtr.Zero);
        keybd_event(0x5B, 0, 2, UIntPtr.Zero);
    }

    public bool Activate(string query)
    {
        var found = IntPtr.Zero;
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle)) return true;
            var builder = new global::System.Text.StringBuilder(512);
            GetWindowText(handle, builder, builder.Capacity);
            var title = builder.ToString();
            if (title.Length == 0) return true;
            if (!title.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
            found = handle;
            return false;
        }, IntPtr.Zero);
        if (found == IntPtr.Zero) return false;
        ShowWindow(found, SwRestore);
        SetForegroundWindow(found);
        return true;
    }

    public IReadOnlyList<string> VisibleWindows()
    {
        var list = new List<string>();
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle)) return true;
            var builder = new global::System.Text.StringBuilder(512);
            GetWindowText(handle, builder, builder.Capacity);
            var title = builder.ToString().Trim();
            if (title.Length > 1) list.Add(title);
            return true;
        }, IntPtr.Zero);
        return list.Distinct().Take(12).ToList();
    }

    public string ForegroundWindowTitle()
    {
        try
        {
            var handle = GetForegroundWindow();
            var builder = new global::System.Text.StringBuilder(512);
            GetWindowText(handle, builder, builder.Capacity);
            return builder.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }
    public IReadOnlyList<string> RunningProcesses() =>
        Process.GetProcesses()
            .Select(process => process.ProcessName.ToLowerInvariant())
            .Distinct()
            .OrderBy(name => name)
            .ToList();

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["хром"] = "chrome",
        ["фаерфокс"] = "firefox",
        ["firefox"] = "firefox",
        ["файрфокс"] = "firefox",
        ["браузер"] = "firefox",
        ["гугл"] = "chrome",
        ["телега"] = "telegram",
        ["телеграм"] = "telegram",
        ["тг"] = "telegram",
        ["блокнот"] = "notepad",
        ["калькулятор"] = "calc",
        ["стим"] = "steam",
        ["дискорд"] = "discord",
        ["ворд"] = "winword",
        ["эксель"] = "excel",
        ["паинт"] = "mspaint",
        ["фотошоп"] = "photoshop",
        ["код"] = "code",
        ["вс код"] = "code",
        ["проводник"] = "explorer",
        ["спотифай"] = "spotify"
    };

    private static bool TryKnownApp(string name, out bool started)
    {
        started = false;
        try
        {
            switch (name.ToLowerInvariant())
            {
                case "steam":
                    var steam = @"C:\Program Files (x86)\Steam\steam.exe";
                    if (!File.Exists(steam)) steam = @"C:\Program Files\Steam\steam.exe";
                    if (File.Exists(steam))
                    {
                        Start(steam);
                        started = true;
                    }
                    else
                    {
                        Start("steam://open/main");
                        started = true;
                    }
                    return true;
                case "epic":
                    var epic = @"C:\Program Files (x86)\Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe";
                    if (!File.Exists(epic)) epic = @"C:\Program Files\Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe";
                    if (File.Exists(epic)) { Start(epic); started = true; }
                    else { Start("com.epicgames.launcher://apps"); started = true; }
                    return true;
                case "fortnite":
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            if (Process.GetProcessesByName("EpicGamesLauncher").Length == 0)
                            {
                                var launcher = @"C:\Program Files\Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe";
                                if (!File.Exists(launcher)) launcher = @"C:\Program Files (x86)\Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe";
                                if (File.Exists(launcher)) Start(launcher);
                                await Task.Delay(9000);
                            }
                            Start("com.epicgames.launcher://apps/Fortnite?action=launch&silent=true");
                        }
                        catch
                        {
                            // Epic может быть не установлен
                        }
                    });
                    started = true;
                    return true;
                case "firefox":
                    var firefox = @"C:\Program Files\Mozilla Firefox\firefox.exe";
                    if (!File.Exists(firefox)) firefox = @"C:\Program Files (x86)\Mozilla Firefox\firefox.exe";
                    if (File.Exists(firefox)) { Start(firefox); started = true; return true; }
                    return false;                case "telegram":
                    var tg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Telegram Desktop", "Telegram.exe");
                    if (File.Exists(tg)) { Start(tg); started = true; return true; }
                    return false;
                case "discord":
                    var discord = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Discord", "Update.exe");
                    if (File.Exists(discord))
                    {
                        Process.Start(new ProcessStartInfo(discord, "--processStart Discord.exe") { UseShellExecute = true });
                        started = true;
                        return true;
                    }
                    return false;
            }
        }
        catch
        {
            // ignore
        }
        return false;
    }
    public bool Launch(string target)
    {
        try
        {
            var name = target.Trim();
            if (Aliases.TryGetValue(name, out var alias)) name = alias;
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(name))
            {
                Start(name);
                return true;
            }

            if (TryKnownApp(name, out var knownStarted) && knownStarted) return true;
            var known = Process.GetProcessesByName(name).FirstOrDefault();
            if (known is not null)
            {
                ShowWindow(known.MainWindowHandle, SwRestore);
                return true;
            }

            try
            {
                Start(name);
                return true;
            }
            catch
            {
                var shortcut = FindShortcut(name);
                if (shortcut is null) return false;
                Start(shortcut);
                return true;
            }
        }
        catch
        {
            return false;
        }
    }

    private static string? FindShortcut(string name)
    {
        try
        {
            var folders = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs")
            };
            foreach (var folder in folders.Where(Directory.Exists))
            {
                var match = Directory.EnumerateFiles(folder, "*.lnk", SearchOption.AllDirectories)
                    .FirstOrDefault(file => Path.GetFileNameWithoutExtension(file)
                        .Contains(name, StringComparison.OrdinalIgnoreCase));
                if (match is not null) return match;
            }
        }
        catch
        {
            // поиск не удался
        }
        return null;
    }

    public bool Close(string target)
    {
        try
        {
            var query = target.Trim().ToLowerInvariant();
            var matched = Process.GetProcesses()
                .Where(process =>
                {
                    try
                    {
                        if (process.ProcessName.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
                        return !string.IsNullOrEmpty(process.MainWindowTitle)
                            && process.MainWindowTitle.Contains(target, StringComparison.OrdinalIgnoreCase);
                    }
                    catch
                    {
                        return false;
                    }
                })
                .ToList();
            foreach (var process in matched)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
            }
            return matched.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    public bool OpenUri(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public int SetAppVolume(string app, int percent) => AudioSessions.SetVolume(app, percent);

    public int GetAppVolume(string app) => AudioSessions.GetVolume(app);

    private static void Start(string target) =>
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
}
