// InstallDetector.cs — installed (Inno Setup) vs. portable (extracted ZIP) K2.
//
// Drives the Settings tab's update-checker: installed copies get "download the
// installer and run it", portable copies just get "download the ZIP" (see
// MainWindow.Settings.cs's RunUpdateCheckAsync).

using System.IO;
using System.Linq;

namespace K2.App.Services;

public static class InstallDetector
{
    /// <summary>True if this copy of K2 was installed via the Inno Setup installer.
    /// Inno always drops an "unins000.exe" (+.dat) next to the installed files —
    /// the portable ZIP never contains one, so its presence is a reliable signal
    /// without touching the registry or the install's AppId.</summary>
    public static bool IsInstalled()
    {
        try
        {
            return Directory.EnumerateFiles(System.AppContext.BaseDirectory, "unins*.exe").Any();
        }
        catch
        {
            return false;
        }
    }
}
