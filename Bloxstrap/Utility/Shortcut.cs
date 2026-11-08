using System.Windows;
using Bloxstrap.Resources;

namespace Bloxstrap.Utility
{
    internal static class Shortcut
    {
        private static GenericTriState _loadStatus = GenericTriState.Unknown;

        private static bool TryCreateWithShell(string exePath, string exeArgs, string lnkPath)
        {
            const string LOG_IDENT = "Shortcut::TryCreateWithShell";

            try
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell");

                if (shellType is null)
                    return false;

                dynamic shell = Activator.CreateInstance(shellType)!;
                dynamic link = shell.CreateShortcut(lnkPath);

                link.TargetPath = exePath;
                link.Arguments = exeArgs;
                link.WorkingDirectory = Path.GetDirectoryName(exePath);
                link.IconLocation = $"{exePath},0";
                link.Save();

                return File.Exists(lnkPath);
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
                return false;
            }
        }

        public static void Create(string exePath, string exeArgs, string lnkPath)
        {
            const string LOG_IDENT = "Shortcut::Create";

            if (File.Exists(lnkPath))
                return;

            try
            {
                // a regular shell link with a real target path: the ShellLink library only fills in an
                // environment variable block, which Windows sometimes can't resolve ("searching for FluxStrap.exe")
                if (!TryCreateWithShell(exePath, exeArgs, lnkPath))
                    ShellLink.Shortcut.CreateShortcut(exePath, exeArgs, exePath, 0).WriteToFile(lnkPath);

                if (_loadStatus != GenericTriState.Successful)
                    _loadStatus = GenericTriState.Successful;
            }
            catch (FileNotFoundException ex)
            {
                App.Logger.WriteLine(LOG_IDENT, $"Failed to create a shortcut for {lnkPath}!");
                App.Logger.WriteException(LOG_IDENT, ex);

                if (_loadStatus == GenericTriState.Failed)
                    return;

                _loadStatus = GenericTriState.Failed;

                Frontend.ShowMessageBox(Strings.Dialog_CannotCreateShortcuts, MessageBoxImage.Information);
            }
        }
    }
}
