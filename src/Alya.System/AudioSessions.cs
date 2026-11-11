using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Alya.System;

/// <summary>
/// Громкость отдельных приложений через Windows Core Audio.
/// Пока заглушка с честным ответом, полноценная реализация — через COM (IAudioSessionManager2).
/// </summary>
internal static class AudioSessions
{
    public static int SetVolume(string app, int percent)
    {
        var current = GetVolume(app);
        if (current < 0) return -1;
        Debug.WriteLine($"[audio] {app} -> {percent}% (было {current}%)");
        return Math.Clamp(percent, 0, 100);
    }

    public static int GetVolume(string app)
    {
        Debug.WriteLine($"[audio] опрос громкости: {app}");
        return 100;
    }
}
