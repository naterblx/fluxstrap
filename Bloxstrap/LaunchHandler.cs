﻿using System.Windows;

using Windows.Win32;
using Windows.Win32.Foundation;

using Bloxstrap.UI.Elements.Dialogs;
using Bloxstrap.Integrations;

namespace Bloxstrap
{
    public static class LaunchHandler
    {
        public static void ProcessNextAction(NextAction action, bool isUnfinishedInstall = false)
        {
            const string LOG_IDENT = "LaunchHandler::ProcessNextAction";

            switch (action)
            {
                case NextAction.LaunchSettings:
                    App.Logger.WriteLine(LOG_IDENT, "Opening settings");
                    LaunchSettings();
                    break;

                case NextAction.LaunchRoblox:
                    App.Logger.WriteLine(LOG_IDENT, "Opening Roblox");
                    LaunchRoblox(LaunchMode.Player);
                    break;

                case NextAction.LaunchRobloxStudio:
                    App.Logger.WriteLine(LOG_IDENT, "Opening Roblox Studio");
                    LaunchRoblox(LaunchMode.Studio);
                    break;

                default:
                    App.Logger.WriteLine(LOG_IDENT, "Closing");
                    App.Terminate(isUnfinishedInstall ? ErrorCode.ERROR_INSTALL_USEREXIT : ErrorCode.ERROR_SUCCESS);
                    break;
            }
        }

        public static void ProcessLaunchArgs()
        {
            const string LOG_IDENT = "LaunchHandler::ProcessLaunchArgs";

            // this order is specific

            if (App.LaunchSettings.UninstallFlag.Active)
            {
                App.Logger.WriteLine(LOG_IDENT, "Opening uninstaller");
                LaunchUninstaller();
            }
            else if (App.LaunchSettings.MenuFlag.Active)
            {
                if (TryUpdateBeforeOpening())
                    return;

                App.Logger.WriteLine(LOG_IDENT, "Opening settings");
                LaunchSettings();
            }
            else if (App.LaunchSettings.WatcherFlag.Active)
            {
                App.Logger.WriteLine(LOG_IDENT, "Opening watcher");
                LaunchWatcher();
            }
            else if (App.LaunchSettings.BackgroundUpdaterFlag.Active)
            {
                App.Logger.WriteLine(LOG_IDENT, "Opening background updater");
                LaunchBackgroundUpdater();
            }
            else if (App.LaunchSettings.RobloxLaunchMode != LaunchMode.None)
            {
                App.Logger.WriteLine(LOG_IDENT, $"Opening bootstrapper ({App.LaunchSettings.RobloxLaunchMode})");
                LaunchRoblox(App.LaunchSettings.RobloxLaunchMode);
            }
            else if (App.LaunchSettings.BloxshadeFlag.Active)
            {
                App.Logger.WriteLine(LOG_IDENT, "Opening Bloxshade");
                LaunchBloxshadeConfig();
            }
            else if (!App.LaunchSettings.QuietFlag.Active)
            {
                if (TryUpdateBeforeOpening())
                    return;

                App.Logger.WriteLine(LOG_IDENT, "Opening menu");
                LaunchMenu();
            }
            else
            {
                App.Logger.WriteLine(LOG_IDENT, "Closing - quiet flag active");
                App.Terminate();
            }
        }

        /// <summary>
        /// The bootstrapper only checks for updates when launching a game, so opening the menu or settings
        /// checks too. Returns true if an update was started, in which case this instance should close.
        /// </summary>
        private static bool TryUpdateBeforeOpening()
        {
            const string LOG_IDENT = "LaunchHandler::TryUpdateBeforeOpening";

#if DEBUG || QA_BUILD
            return false;
#else
            if (!App.Settings.Prop.CheckForUpdates || !App.HasProjectRepository || App.LaunchSettings.UpgradeFlag.Active)
                return false;

            // another instance is likely updating or running in the background
            if (Process.GetProcessesByName(App.ProjectName).Length > 1)
                return false;

            // don't hold the menu up for long when github is slow or the pc is offline
            var releaseTask = Task.Run(App.GetLatestRelease);

            if (!releaseTask.Wait(TimeSpan.FromSeconds(4)) || releaseTask.Result is not GithubRelease release)
            {
                App.Logger.WriteLine(LOG_IDENT, "Could not get the latest release in time");
                return false;
            }

            var comparison = Utilities.CompareVersions(App.Version, release.TagName);

            if (App.IsProductionBuild && comparison == VersionComparison.Equal || comparison == VersionComparison.GreaterThan)
            {
                App.Logger.WriteLine(LOG_IDENT, $"Up to date ({App.Version}, latest is {release.TagName})");
                return false;
            }

            if (release.Assets is null || !release.Assets.Any())
                return false;

            App.Logger.WriteLine(LOG_IDENT, $"{release.TagName} is available (running {App.Version})");

            var answer = Frontend.ShowMessageBox(
                $"FluxStrap {release.TagName} is available (you have {App.Version}).\n\nWould you like to update now?",
                MessageBoxImage.Question,
                MessageBoxButton.YesNo
            );

            if (answer != MessageBoxResult.Yes)
                return false;

            try
            {
                var asset = release.Assets[0];
                string downloadLocation = Path.Combine(Paths.TempUpdates, asset.Name);

                Directory.CreateDirectory(Paths.TempUpdates);

                App.Logger.WriteLine(LOG_IDENT, $"Downloading {release.TagName}...");

                Task.Run(async () =>
                {
                    using var response = await App.HttpClient.GetAsync(asset.BrowserDownloadUrl);
                    response.EnsureSuccessStatusCode();

                    await using var fileStream = new FileStream(downloadLocation, FileMode.Create, FileAccess.Write);
                    await response.Content.CopyToAsync(fileStream);
                }).GetAwaiter().GetResult();

                // the new version installs itself over this one, then carries on with the same arguments
                var startInfo = new ProcessStartInfo(downloadLocation);
                startInfo.ArgumentList.Add("-upgrade");

                foreach (string arg in App.LaunchSettings.Args)
                    startInfo.ArgumentList.Add(arg);

                App.Settings.Save();

                new InterProcessLock("AutoUpdater");

                Process.Start(startInfo);
                App.Terminate();

                return true;
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, "Could not download the update");
                App.Logger.WriteException(LOG_IDENT, ex);

                Frontend.ShowMessageBox(string.Format(Strings.Bootstrapper_AutoUpdateFailed, release.TagName), MessageBoxImage.Information);
                Utilities.ShellExecute($"https://github.com/{App.ProjectRepository}/releases/latest");

                return false;
            }
#endif
        }

        public static void LaunchInstaller()
        {
            using var interlock = new InterProcessLock("Installer");

            if (!interlock.IsAcquired)
            {
                Frontend.ShowMessageBox(Strings.Dialog_AlreadyRunning_Installer, MessageBoxImage.Stop);
                App.Terminate();
                return;
            }

            if (App.LaunchSettings.UninstallFlag.Active)
            {
                Frontend.ShowMessageBox(Strings.Bootstrapper_FirstRunUninstall, MessageBoxImage.Error);
                App.Terminate(ErrorCode.ERROR_INVALID_FUNCTION);
                return;
            }

            if (App.LaunchSettings.QuietFlag.Active)
            {
                var installer = new Installer();

                if (!installer.CheckInstallLocation())
                    App.Terminate(ErrorCode.ERROR_INSTALL_FAILURE);

                installer.DoInstall();

                interlock.Dispose();

                ProcessLaunchArgs();
            }
            else
            {
#if QA_BUILD
                Frontend.ShowMessageBox("You are about to install a QA build of Bloxstrap. The red window border indicates that this is a QA build.\n\nQA builds are handled completely separately of your standard installation, like a virtual environment.", MessageBoxImage.Information);
#endif

                new LanguageSelectorDialog().ShowDialog();

                var installer = new UI.Elements.Installer.MainWindow();
                installer.ShowDialog();

                interlock.Dispose();

                ProcessNextAction(installer.CloseAction, !installer.Finished);
            }

        }

        public static void LaunchUninstaller()
        {
            using var interlock = new InterProcessLock("Uninstaller");

            if (!interlock.IsAcquired)
            {
                Frontend.ShowMessageBox(Strings.Dialog_AlreadyRunning_Uninstaller, MessageBoxImage.Stop);
                App.Terminate();
                return;
            }

            bool confirmed = false;
            bool keepData = true;

            if (App.LaunchSettings.QuietFlag.Active)
            {
                confirmed = true;
            }
            else
            {
                var dialog = new UninstallerDialog();
                dialog.ShowDialog();

                confirmed = dialog.Confirmed;
                keepData = dialog.KeepData;
            }

            if (!confirmed)
            {
                App.Terminate();
                return;
            }

            Installer.DoUninstall(keepData);

            Frontend.ShowMessageBox(Strings.Bootstrapper_SuccessfullyUninstalled, MessageBoxImage.Information);

            App.Terminate();
        }

        public static void LaunchSettings()
        {
            const string LOG_IDENT = "LaunchHandler::LaunchSettings";

            using var interlock = new InterProcessLock("Settings");

            if (interlock.IsAcquired)
            {
                bool showAlreadyRunningWarning = Process.GetProcessesByName(App.ProjectName).Length > 1;

                var window = new UI.Elements.Settings.MainWindow(showAlreadyRunningWarning);

                // typically we'd use Show(), but we need to block to ensure IPL stays in scope
                window.ShowDialog();
            }
            else
            {
                App.Logger.WriteLine(LOG_IDENT, "Found an already existing menu window");

                var process = Utilities.GetProcessesSafe().Where(x => x.MainWindowTitle == Strings.Menu_Title).FirstOrDefault();

                if (process is not null)
                    PInvoke.SetForegroundWindow((HWND)process.MainWindowHandle);

                App.Terminate();
            }
        }

        public static void LaunchMenu()
        {
            var dialog = new LaunchMenuDialog();
            dialog.ShowDialog();

            ProcessNextAction(dialog.CloseAction);
        }

        public static void LaunchRoblox(LaunchMode launchMode)
        {
            const string LOG_IDENT = "LaunchHandler::LaunchRoblox";

            if (launchMode == LaunchMode.None)
                throw new InvalidOperationException("No Roblox launch mode set");

            if (!File.Exists(Path.Combine(Paths.System, "mfplat.dll")))
            {
                Frontend.ShowMessageBox(Strings.Bootstrapper_WMFNotFound, MessageBoxImage.Error);

                if (!App.LaunchSettings.QuietFlag.Active)
                    Utilities.ShellExecute("https://support.microsoft.com/en-us/topic/media-feature-pack-list-for-windows-n-editions-c1c6fffa-d052-8338-7a79-a4bb980a700a");

                App.Terminate(ErrorCode.ERROR_FILE_NOT_FOUND);
            }

            if (App.Settings.Prop.ConfirmLaunches && Utilities.IsRobloxRunning() && launchMode != LaunchMode.Studio)
            {
                // this currently doesn't work very well since it relies on checking the existence of the singleton mutex
                // which often hangs around for a few seconds after the window closes
                // it would be better to have this rely on the activity tracker when we implement IPC in the planned refactoring

                var result = Frontend.ShowMessageBox(Strings.Bootstrapper_ConfirmLaunch, MessageBoxImage.Warning, MessageBoxButton.YesNo);

                if (result != MessageBoxResult.Yes)
                {
                    App.Terminate();
                    return;
                }
            }

            // start bootstrapper and show the bootstrapper modal if we're not running silently
            App.Logger.WriteLine(LOG_IDENT, "Initializing bootstrapper");
            App.Bootstrapper = new Bootstrapper(launchMode);
            IBootstrapperDialog? dialog = null;

            if (!App.LaunchSettings.QuietFlag.Active)
            {
                App.Logger.WriteLine(LOG_IDENT, "Initializing bootstrapper dialog");
                dialog = App.Settings.Prop.BootstrapperStyle.GetNew();
                App.Bootstrapper.Dialog = dialog;
                dialog.Bootstrapper = App.Bootstrapper;
            }

            Task.Run(App.Bootstrapper.Run).ContinueWith(t =>
            {
                App.Logger.WriteLine(LOG_IDENT, "Bootstrapper task has finished");

                if (t.IsFaulted)
                {
                    App.Logger.WriteLine(LOG_IDENT, "An exception occurred when running the bootstrapper");

                    if (t.Exception is not null)
                        App.FinalizeExceptionHandling(t.Exception);
                }

                App.Terminate();
            });

            dialog?.ShowBootstrapper();

            App.Logger.WriteLine(LOG_IDENT, "Exiting");
        }

        public static void LaunchWatcher()
        {
            const string LOG_IDENT = "LaunchHandler::LaunchWatcher";

            // this whole topology is a bit confusing, bear with me:
            // main thread: strictly UI only, handles showing of the notification area icon, context menu, server details dialog
            // - server information task: queries server location, invoked if either the explorer notification is shown or the server details dialog is opened
            // - discord rpc thread: handles rpc connection with discord
            //    - discord rich presence tasks: handles querying and displaying of game information, invoked on activity watcher events
            // - watcher task: runs activity watcher + waiting for roblox to close, terminates when it has

            var watcher = new Watcher();

            Task.Run(watcher.Run).ContinueWith(t =>
            {
                App.Logger.WriteLine(LOG_IDENT, "Watcher task has finished");

                watcher.Dispose();

                if (t.IsFaulted)
                {
                    App.Logger.WriteLine(LOG_IDENT, "An exception occurred when running the watcher");

                    if (t.Exception is not null)
                        App.FinalizeExceptionHandling(t.Exception);
                }

                // shouldnt this be done after client closes?
                if (App.Settings.Prop.CleanerOptions != CleanerOptions.Never)
                    Cleaner.DoCleaning();

                App.Terminate();
            });
        }

        public static void LaunchBloxshadeConfig()
        {
            const string LOG_IDENT = "LaunchHandler::LaunchBloxshade";

            App.Logger.WriteLine(LOG_IDENT, "Showing unsupported warning");

            new BloxshadeDialog().ShowDialog();
            App.SoftTerminate();
        }

        public static void LaunchBackgroundUpdater()
        {
            const string LOG_IDENT = "LaunchHandler::LaunchBackgroundUpdater";

            // Activate some LaunchFlags we need
            App.LaunchSettings.QuietFlag.Active = true;
            App.LaunchSettings.NoLaunchFlag.Active = true;

            App.Logger.WriteLine(LOG_IDENT, "Initializing bootstrapper");
            App.Bootstrapper = new Bootstrapper(LaunchMode.Player)
            {
                MutexName = $"{App.ProjectName}-BackgroundUpdater",
                QuitIfMutexExists = true
            };

            CancellationTokenSource cts = new CancellationTokenSource();

            Task.Run(() =>
            {
                App.Logger.WriteLine(LOG_IDENT, "Started event waiter");
                using (EventWaitHandle handle = new EventWaitHandle(false, EventResetMode.AutoReset, $"{App.ProjectName}-BackgroundUpdaterKillEvent"))
                    handle.WaitOne();

                App.Logger.WriteLine(LOG_IDENT, "Received close event, killing it all!");
                App.Bootstrapper.Cancel();
            }, cts.Token);

            Task.Run(App.Bootstrapper.Run).ContinueWith(t =>
            {
                App.Logger.WriteLine(LOG_IDENT, "Bootstrapper task has finished");
                cts.Cancel(); // stop event waiter

                if (t.IsFaulted)
                {
                    App.Logger.WriteLine(LOG_IDENT, "An exception occurred when running the bootstrapper");

                    if (t.Exception is not null)
                        App.FinalizeExceptionHandling(t.Exception);
                }

                App.Terminate();
            });

            App.Logger.WriteLine(LOG_IDENT, "Exiting");
        }
    }
}
