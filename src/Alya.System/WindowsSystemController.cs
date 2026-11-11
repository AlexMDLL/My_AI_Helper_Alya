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

    public IReadOnlyList<string> RunningProcesses() =>
        Process.GetProcesses()
            .Select(process => process.ProcessName.ToLowerInvariant())
            .Distinct()
            .OrderBy(name => name)
            .ToList();

    public bool Launch(string target)
    {
        try
        {
            var name = target.Trim();
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(name))
            {
                Start(name);
                return true;
            }

            var known = Process.GetProcessesByName(name).FirstOrDefault();
            if (known is not null)
            {
                ShowWindow(known.MainWindowHandle, SwRestore);
                return true;
            }

            Start(name);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool Close(string target)
    {
        try
        {
            var processes = Process.GetProcessesByName(target);
            foreach (var process in processes)
            {
                process.Kill(entireProcessTree: true);
            }
            return processes.Length > 0;
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
