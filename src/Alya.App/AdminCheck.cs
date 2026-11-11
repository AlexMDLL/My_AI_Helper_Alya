using System.Runtime.InteropServices;

namespace Alya.App;

internal static class AdminCheck
{
    [DllImport("shell32.dll", SetLastError = true)]
    private static extern bool IsUserAnAdmin();

    public static bool IsElevated()
    {
        try
        {
            return IsUserAnAdmin();
        }
        catch
        {
            return false;
        }
    }
}