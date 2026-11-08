using System.IO;
using System.Windows;
using System.Xml.Linq;
using Bloxstrap.AppData;
using Microsoft.Win32;

namespace Bloxstrap
{
    internal class Installer
    {
        /// <summary>
        /// Should this version automatically open the release notes page?
        /// Recommended for major updates only.
        /// </summary>
        private const bool OpenReleaseNotes = false;

        private static string DesktopShortcut => Path.Combine(Paths.Desktop, $"{App.ProjectName}.lnk");

        private static string StartMenuShortcut => Path.Combine(Paths.WindowsStartMenu, $"{App.ProjectName}.lnk");

        public string BloxstrapInstallDirectory = Path.Combine(Paths.LocalAppData, "Bloxstrap"); // default directory for bloxstrap
                                                                                                 // TODO dynamically fetch from uninstall/player registry keys
        public string InstallLocation = Path.Combine(Paths.LocalAppData, App.ProjectName);

        public bool ExistingDataPresent => File.Exists(Path.Combine(InstallLocation, "Settings.json"));

        public bool CreateDesktopShortcuts = true;

        public bool CreateStartMenuShortcuts = true;

        // off by default: bloxstrap mods and flags are made for modern roblox and can break the older fluxer clients
        public bool ImportSettings = false;

        public bool IsImplicitInstall = false;

        public string InstallLocationError { get; set; } = "";

        // anything we want copied should be put in here
        // root directory only
        public string[] FilesForImporting = {
            "CustomThemes", // from feature/custom-bootstrappers
            "Modifications",
            "Settings.json"
        };

        public void DoInstall()
        {
            const string LOG_IDENT = "Installer::DoInstall";

            App.Logger.WriteLine(LOG_IDENT, "Beginning installation");

            // should've been created earlier from the write test anyway
            Directory.CreateDirectory(InstallLocation);

            Paths.Initialize(InstallLocation);

            if (!IsImplicitInstall)
            {
                Filesystem.AssertReadOnly(Paths.Application);

                try
                {
                    File.Copy(Paths.Process, Paths.Application, true);
                }
                catch (Exception ex)
                {
                    App.Logger.WriteLine(LOG_IDENT, "Could not overwrite executable");
                    App.Logger.WriteException(LOG_IDENT, ex);

                    Frontend.ShowMessageBox(Strings.Installer_Install_CannotOverwrite, MessageBoxImage.Error);
                    App.Terminate(ErrorCode.ERROR_INSTALL_FAILURE);
                }
            }

            using (var uninstallKey = Registry.CurrentUser.CreateSubKey(App.UninstallKey))
            {
                uninstallKey.SetValueSafe("DisplayIcon", $"{Paths.Application},0");
                uninstallKey.SetValueSafe("DisplayName", App.ProjectName);

                uninstallKey.SetValueSafe("DisplayVersion", App.Version);

                if (uninstallKey.GetValue("InstallDate") is null)
                    uninstallKey.SetValueSafe("InstallDate", DateTime.Now.ToString("yyyyMMdd"));

                uninstallKey.SetValueSafe("InstallLocation", Paths.Base);
                uninstallKey.SetValueSafe("NoRepair", 1);
                uninstallKey.SetValueSafe("Publisher", App.ProjectOwner);
                uninstallKey.SetValueSafe("ModifyPath", $"\"{Paths.Application}\" -settings");
                uninstallKey.SetValueSafe("QuietUninstallString", $"\"{Paths.Application}\" -uninstall -quiet");
                uninstallKey.SetValueSafe("UninstallString", $"\"{Paths.Application}\" -uninstall");
                uninstallKey.SetValueSafe("HelpLink", App.ProjectHelpLink);
                uninstallKey.SetValueSafe("URLInfoAbout", App.ProjectSupportLink);
                uninstallKey.SetValueSafe("URLUpdateInfo", App.ProjectDownloadLink);
            }

            WindowsRegistry.RegisterApis();

            // only register player, for the scenario where the user installs bloxstrap, closes it,
            // and then launches from the website expecting it to work
            // studio can be implicitly registered when it's first launched manually
            WindowsRegistry.RegisterPlayer();

            if (CreateDesktopShortcuts)
                Shortcut.Create(Paths.Application, "", DesktopShortcut);

            if (CreateStartMenuShortcuts)
                Shortcut.Create(Paths.Application, "", StartMenuShortcut);

            if (ImportSettings)
            {
                // we dont have to worry about directories messing up
                // if something doenst exist fishstrap will recreate the file/directory
                try
                {
                    ImportSettingsFromBloxstrap();
                } catch (Exception ex)
                {
                    Frontend.ShowMessageBox(
                        String.Format(Strings.Installer_FailedToImportSettings, ex.Message),
                        MessageBoxImage.Error,
                        MessageBoxButton.OK
                    );
                }
            }

            // existing configuration persisting from an earlier install
            // or from importing settings
            App.Settings.Load(false);
            App.State.Load(false);
            App.FastFlags.Load(false);

            if (App.IsStudioVisible)
                WindowsRegistry.RegisterStudio();

            App.Settings.Save();

            App.Logger.WriteLine(LOG_IDENT, "Installation finished");

        }

        private bool ValidateLocation()
        {
            // prevent from installing to the root of a drive
            if (InstallLocation.Length <= 3)
                return false;

            // unc path, just to be safe
            if (InstallLocation.StartsWith("\\\\"))
                return false;

            if (InstallLocation.StartsWith(Path.GetTempPath(), StringComparison.InvariantCultureIgnoreCase)
                || InstallLocation.Contains("\\Temp\\", StringComparison.InvariantCultureIgnoreCase))
                return false;

            // prevent from installing to a onedrive folder
            if (InstallLocation.Contains("OneDrive", StringComparison.InvariantCultureIgnoreCase))
                return false;

            // prevent from installing to an essential user profile folder (e.g. Documents, Downloads, Contacts idk)
            if (String.Compare(Directory.GetParent(InstallLocation)?.FullName, Paths.UserProfile, StringComparison.InvariantCultureIgnoreCase) == 0)
                return false;

            // prevent from installing into the program files folder
            if (InstallLocation.Contains("Program Files"))
                return false;

            // prevent issues with settings importing
            if (InstallLocation.Contains("Local\\Bloxstrap"))
                return false;

            if (InstallLocation.Contains("WindowsApps"))
                return false;

            return true;
        }

        public bool CheckInstallLocation()
        {
            if (string.IsNullOrEmpty(InstallLocation))
            {
                InstallLocationError = Strings.Menu_InstallLocation_NotSet;
            }
            else if (!ValidateLocation())
            {
                InstallLocationError = Strings.Menu_InstallLocation_CantInstall;
            }
            else
            {
                if (!IsImplicitInstall 
                    && !InstallLocation.EndsWith(App.ProjectName, StringComparison.InvariantCultureIgnoreCase)
                    && Directory.Exists(InstallLocation)
                    && Directory.EnumerateFileSystemEntries(InstallLocation).Any())
                {
                    string suggestedChange = Path.Combine(InstallLocation, App.ProjectName);

                    MessageBoxResult result = Frontend.ShowMessageBox(
                        String.Format(Strings.Menu_InstallLocation_NotEmpty, suggestedChange),
                        MessageBoxImage.Warning,
                        MessageBoxButton.YesNoCancel,
                        MessageBoxResult.Yes
                    );

                    if (result == MessageBoxResult.Yes)
                        InstallLocation = suggestedChange;
                    else if (result == MessageBoxResult.Cancel || result == MessageBoxResult.None)
                        return false;
                }

                try
                {
                    // check if we can write to the directory (a bit hacky but eh)
                    string testFile = Path.Combine(InstallLocation, $"{App.ProjectName}WriteTest.txt");

                    Directory.CreateDirectory(InstallLocation);
                    File.WriteAllText(testFile, "");
                    File.Delete(testFile);
                }
                catch (UnauthorizedAccessException)
                {
                    InstallLocationError = Strings.Menu_InstallLocation_NoWritePerms;
                }
                catch (Exception ex)
                {
                    InstallLocationError = ex.Message;
                }
            }

            return String.IsNullOrEmpty(InstallLocationError);
        }

        public static void DoUninstall(bool keepData)
        {
            const string LOG_IDENT = "Installer::DoUninstall";

            var processes = new List<Process>();

            // only close clients that run out of our own folder, not the official launcher's or real roblox
            string versionsPrefix = Paths.Versions.TrimEnd('\\') + "\\";

            foreach (string name in new[] { App.FluxerPlayerAppName, App.RobloxPlayerAppName, App.RobloxStudioAppName })
            {
                foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(name)))
                {
                    try
                    {
                        if (process.MainModule?.FileName?.StartsWith(versionsPrefix, StringComparison.OrdinalIgnoreCase) == true)
                            processes.Add(process);
                    }
                    catch (Exception) { }
                }
            }

            // prompt to shutdown fluxer if its currently running
            if (processes.Any())
            {
                var result = Frontend.ShowMessageBox(
                    Strings.Bootstrapper_Uninstall_RobloxRunning,
                    MessageBoxImage.Information,
                    MessageBoxButton.OKCancel,
                    MessageBoxResult.OK
                );

                if (result != MessageBoxResult.OK)
                {
                    App.Terminate(ErrorCode.ERROR_CANCELLED);
                    return;
                }

                try
                {
                    foreach (var process in processes)
                    {
                        process.Kill();
                        process.Close();
                    }
                }
                catch (Exception ex)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"Failed to close process! {ex}");
                }
            }

            // hand the fluxer links back to the official launcher if it's still around
            WindowsRegistry.RestoreFluxerLauncher();

            Registry.CurrentUser.DeleteSubKey(App.ApisKey);

            var cleanupSequence = new List<Action>
            {
                () =>
                {
                    foreach (var file in Directory.GetFiles(Paths.Desktop).Where(x => x.EndsWith("lnk")))
                    {
                        var shortcut = ShellLink.Shortcut.ReadFromFile(file);

                        if (shortcut.ExtraData.EnvironmentVariableDataBlock?.TargetUnicode == Paths.Application)
                            File.Delete(file);
                    }
                },

                // shortcuts made by the windows shell don't have the block checked above
                () => File.Delete(DesktopShortcut),

                () => File.Delete(StartMenuShortcut),

                () => Directory.Delete(Paths.Versions, true),

                () => Directory.Delete(Paths.Downloads, true),

                () => File.Delete(App.State.FileLocation),

                () => File.Delete(App.RobloxState.FileLocation)
            };


            if (!keepData)
            {
                cleanupSequence.AddRange(new List<Action>
                {
                    () => Directory.Delete(Paths.Modifications, true),
                    () => Directory.Delete(Paths.Logs, true),

                    () => File.Delete(App.Settings.FileLocation)
                });
            }

            bool deleteFolder = Directory.GetFiles(Paths.Base).Length <= 3;

            if (deleteFolder)
                cleanupSequence.Add(() => Directory.Delete(Paths.Base, true));

            cleanupSequence.Add(() => Registry.CurrentUser.DeleteSubKey(App.UninstallKey));

            foreach (var process in cleanupSequence)
            {
                try
                {
                    process();
                }
                catch (Exception ex)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"Encountered exception when running cleanup sequence (#{cleanupSequence.IndexOf(process)})");
                    App.Logger.WriteException(LOG_IDENT, ex);
                }
            }

            if (Directory.Exists(Paths.Base))
            {
                // this is definitely one of the workaround hacks of all time

                string deleteCommand;

                if (deleteFolder)
                    deleteCommand = $"del /Q \"{Paths.Base}\\*\" && rmdir \"{Paths.Base}\"";
                else
                    deleteCommand = $"del /Q \"{Paths.Application}\"";

                Process.Start(new ProcessStartInfo()
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c timeout 5 && {deleteCommand}",
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
            }
        }

        public static void HandleUpgrade()
        {
            const string LOG_IDENT = "Installer::HandleUpgrade";

            if (!File.Exists(Paths.Application) || Paths.Process == Paths.Application)
                return;

            // 2.0.0 downloads updates to <BaseFolder>/Updates so lol
            bool isAutoUpgrade = App.LaunchSettings.UpgradeFlag.Active
                || Paths.Process.StartsWith(Path.Combine(Paths.Base, "Updates"))
                || Paths.Process.StartsWith(Path.Combine(Paths.LocalAppData, "Temp"))
                || Paths.Process.StartsWith(Paths.TempUpdates);

            var existingVer = FileVersionInfo.GetVersionInfo(Paths.Application).ProductVersion;
            var currentVer = FileVersionInfo.GetVersionInfo(Paths.Process).ProductVersion;

            if (MD5Hash.FromFile(Paths.Process) == MD5Hash.FromFile(Paths.Application))
                return;

            if (currentVer is not null && existingVer is not null && Utilities.CompareVersions(currentVer, existingVer) == VersionComparison.LessThan)
            {
                var result = Frontend.ShowMessageBox(
                    Strings.InstallChecker_VersionLessThanInstalled,
                    MessageBoxImage.Question,
                    MessageBoxButton.YesNo
                );

                if (result != MessageBoxResult.Yes)
                    return;
            }

            // silently upgrade version if the command line flag is set or if we're launching from an auto update
            if (!isAutoUpgrade)
            {
                var result = Frontend.ShowMessageBox(
                    Strings.InstallChecker_VersionDifferentThanInstalled,
                    MessageBoxImage.Question,
                    MessageBoxButton.YesNo
                );

                if (result != MessageBoxResult.Yes)
                    return;
            }

            App.Logger.WriteLine(LOG_IDENT, "Doing upgrade");

            Filesystem.AssertReadOnly(Paths.Application);

            using (var ipl = new InterProcessLock("AutoUpdater", TimeSpan.FromSeconds(5)))
            {
                if (!ipl.IsAcquired)
                {
                    App.Logger.WriteLine(LOG_IDENT, "Failed to update! (Could not obtain singleton mutex)");
                    return;
                }
            }

            // prior to 2.8.0, auto-updating was handled with this... bruteforce method
            // now it's handled with the system mutex you see above, but we need to keep this logic for <2.8.0 versions
            for (int i = 1; i <= 10; i++)
            {
                try
                {
                    File.Copy(Paths.Process, Paths.Application, true);
                    break;
                }
                catch (Exception ex)
                {
                    if (i == 1)
                    {
                        App.Logger.WriteLine(LOG_IDENT, "Waiting for write permissions to update version");
                    }
                    else if (i == 10)
                    {
                        App.Logger.WriteLine(LOG_IDENT, "Failed to update! (Could not get write permissions after 10 tries/5 seconds)");
                        App.Logger.WriteException(LOG_IDENT, ex);
                        return;
                    }

                    Thread.Sleep(500);
                }
            }

            using (var uninstallKey = Registry.CurrentUser.CreateSubKey(App.UninstallKey))
            {
                uninstallKey.SetValueSafe("DisplayVersion", App.Version);

                uninstallKey.SetValueSafe("Publisher", App.ProjectOwner);
                uninstallKey.SetValueSafe("HelpLink", App.ProjectHelpLink);
                uninstallKey.SetValueSafe("URLInfoAbout", App.ProjectSupportLink);
                uninstallKey.SetValueSafe("URLUpdateInfo", App.ProjectDownloadLink);
            }

            // update migrations
            // fishstrap's own migrations (for versions below 2.9) don't apply to fluxstrap, which started at 1.0.0

            App.Settings.Save();
            App.FastFlags.Save();
            App.State.Save();
            App.RobloxState.Save();

            if (currentVer is null)
                return;

            if (isAutoUpgrade)
            {
#pragma warning disable CS0162 // Unreachable code detected
                if (OpenReleaseNotes && App.HasProjectRepository)
                    Utilities.ShellExecute($"https://github.com/{App.ProjectRepository}/releases/tag/v{currentVer}");
#pragma warning restore CS0162 // Unreachable code detected
            }
            else
            {
                Frontend.ShowMessageBox(
                    string.Format(Strings.InstallChecker_Updated, currentVer),
                    MessageBoxImage.Information,
                    MessageBoxButton.OK
                );
            }
        }

        public void ImportSettingsFromBloxstrap()
        {
            const string LOG_IDENT = "Installer::ImportSettings";

            if (!Directory.Exists(BloxstrapInstallDirectory))
            {
                Frontend.ShowMessageBox(Strings.Installer_InstallationNotFound, MessageBoxImage.Exclamation);
                return;
            } // bloxstrap default directory is not present

            foreach (string FileName in FilesForImporting)
            {
                string Source = Path.Combine(BloxstrapInstallDirectory, FileName);
                if (!Directory.Exists(Source) && !File.Exists(Source))
                    continue; // customthemes

                FileAttributes Attributes = File.GetAttributes(Source);
                bool IsDirectory = Attributes.HasFlag(FileAttributes.Directory);

                App.Logger.WriteLine(LOG_IDENT, $"Found file {Source}, IsDirectory: {IsDirectory}");

                if (IsDirectory)
                {
                    // delete existing file from fishstrap folder
                    string ExistingFile = Path.Combine(InstallLocation, FileName);
                    if (Directory.Exists(ExistingFile))
                    {
                        App.Logger.WriteLine(LOG_IDENT, $"Deleting existing {FileName}...");
                        Directory.Delete(ExistingFile, true);
                    }

                    // https://stackoverflow.com/questions/58744/copy-the-entire-contents-of-a-directory-in-c-sharp
                    // we could use Directory.Move but that deletes the directory from bloxstrap folder
                    // instead we will use this

                    // create the directory
                    Directory.CreateDirectory(ExistingFile);

                    // Now Create all of the directories
                    foreach (string dirPath in Directory.GetDirectories(Source, "*", SearchOption.AllDirectories))
                    {
                        Directory.CreateDirectory(dirPath.Replace(Source, ExistingFile));
                    }

                    // Copy all the files & Replaces any files with the same name
                    foreach (string newPath in Directory.GetFiles(Source, "*.*", SearchOption.AllDirectories))
                    {
                        File.Copy(newPath, newPath.Replace(Source, ExistingFile), true);
                    }
                } else
                {
                    string FileLocation = Path.Combine(InstallLocation, FileName);
                    // we dont have to delete the file here
                    // we can simply override it
                    File.Copy(Source, FileLocation, true);
                    App.Logger.WriteLine(LOG_IDENT, $"Overridding {FileName} in InstallLocation");
                }
            }

            App.Logger.WriteLine(LOG_IDENT, $"Importing succeded");

            // these happen later on in installation process
            // App.Settings.Load(false);
            // App.State.Load(false);
            // App.FastFlags.Load(false);
        }
    }
}
