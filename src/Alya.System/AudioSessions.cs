using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Alya.System;

/// <summary>
/// Громкость отдельных приложений через Windows Core Audio (IAudioSessionManager2 → ISimpleAudioVolume).
/// Работает как pycaw, но нативно и без Python.
/// </summary>
internal static class AudioSessions
{
    public static int SetVolume(string app, int percent)
    {
        var target = Math.Clamp(percent, 0, 100);
        var sessions = EnumerateSessions(app);
        if (sessions.Count == 0) return -1;
        foreach (var volume in sessions)
        {
            volume.SetMasterVolume(target / 100f, Guid.Empty);
        }
        return target;
    }

    public static int GetVolume(string app)
    {
        var sessions = EnumerateSessions(app);
        if (sessions.Count == 0) return -1;
        var values = new List<float>();
        foreach (var volume in sessions)
        {
            volume.GetMasterVolume(out var level);
            values.Add(level);
        }
        return (int)Math.Round(values.Average() * 100);
    }

    public static bool SetMute(string app, bool mute)
    {
        var sessions = EnumerateSessions(app);
        if (sessions.Count == 0) return false;
        foreach (var volume in sessions)
        {
            volume.SetMute(mute, Guid.Empty);
        }
        return true;
    }

    private static List<ISimpleAudioVolume> EnumerateSessions(string app)
    {
        var result = new List<ISimpleAudioVolume>();
        var filter = app.ToLowerInvariant().Replace(".exe", string.Empty);
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        object? managerObject = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            enumerator.GetDefaultAudioEndpoint(0, 1, out device);
            var iid = typeof(IAudioSessionManager2).GUID;
            device.Activate(ref iid, 1, IntPtr.Zero, out managerObject);
            var manager = (IAudioSessionManager2)managerObject;
            manager.GetSessionEnumerator(out var sessionEnum);
            sessionEnum.GetCount(out var count);
            for (var i = 0; i < count; i++)
            {
                sessionEnum.GetSession(i, out var control);
                if (control is not IAudioSessionControl2 control2) continue;
                control2.GetProcessId(out var pid);
                string name;
                try
                {
                    name = Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant();
                }
                catch
                {
                    continue;
                }
                if (!name.Contains(filter) && !filter.Contains(name)) continue;
                if (control2 is ISimpleAudioVolume volume)
                {
                    result.Add(volume);
                }
            }
        }
        catch (Exception error)
        {
            Debug.WriteLine($"[audio] {error.Message}");
        }
        finally
        {
            if (managerObject is not null && Marshal.IsComObject(managerObject)) Marshal.ReleaseComObject(managerObject);
            if (device is not null && Marshal.IsComObject(device)) Marshal.ReleaseComObject(device);
            if (enumerator is not null && Marshal.IsComObject(enumerator)) Marshal.ReleaseComObject(enumerator);
        }
        return result;
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        int GetDevice(string id, out IMMDevice device);
        int RegisterEndpointNotificationCallback(IntPtr client);
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int classContext, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        int OpenPropertyStore(int access, out IntPtr properties);
        int GetId(out IntPtr id);
        int GetState(out int state);
    }

    [ComImport]
    [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        int GetAudioSessionControl(ref Guid sessionGuid, int streamFlags, out IntPtr sessionControl);
        int GetSimpleAudioVolume(ref Guid sessionGuid, int streamFlags, out ISimpleAudioVolume volume);
        int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
        int RegisterSessionNotification(IntPtr notification);
        int UnregisterSessionNotification(IntPtr notification);
        int RegisterDuckNotification(string sessionId, IntPtr notification);
        int UnregisterDuckNotification(IntPtr notification);
    }

    [ComImport]
    [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        int GetCount(out int count);
        int GetSession(int index, [MarshalAs(UnmanagedType.Interface)] out object session);
    }

    [ComImport]
    [Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        int GetState(out int state);
        int GetDisplayName(out IntPtr name);
        int SetDisplayName(string name, ref Guid eventContext);
        int GetIconPath(out IntPtr path);
        int SetIconPath(string path, ref Guid eventContext);
        int GetGroupingParam(out Guid groupingParam);
        int SetGroupingParam(ref Guid groupingParam, ref Guid eventContext);
        int RegisterAudioSessionNotification(IntPtr notification);
        int UnregisterAudioSessionNotification(IntPtr notification);
        int GetSessionIdentifier(out IntPtr identifier);
        int GetSessionInstanceIdentifier(out IntPtr identifier);
        int GetProcessId(out uint processId);
        int IsSystemSounds(out int systemSounds);
        int SetDuckInformation(IntPtr duckInfo);
        int GetDuckInformation(out IntPtr duckInfo);
        int Start();
        int Stop();
    }

    [ComImport]
    [Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        int SetMasterVolume(float level, Guid eventContext);
        int GetMasterVolume(out float level);
        int SetMute(bool mute, Guid eventContext);
        int GetMute(out bool mute);
    }
}
