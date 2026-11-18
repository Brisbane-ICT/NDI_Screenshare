using Microsoft.Win32;

namespace NdiScreenshare.Infra;

public static class Autostart
{
    private const string Key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "NDI_Screenshare";

    public static bool IsEnabled()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(Key, false);
            return k?.GetValue(Name) != null;
        }
        catch { return false; }
    }

    public static void SetEnabled(bool enable)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(Key, true)!;
            if (enable)
            {
                var exe = Application.ExecutablePath;
                k.SetValue(Name, $"\"{exe}\"");
            }
            else k.DeleteValue(Name, false);
        }
        catch { }
    }
}
