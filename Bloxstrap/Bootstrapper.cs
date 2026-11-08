// To debug the automatic updater:
// - Uncomment the definition below
// - Publish the executable
// - Launch the executable (click no when it asks you to upgrade)
// - Launch Fluxer (for testing web launches, run it from the command prompt)
// - To re-test the same executable, delete it from the installation folder

// #define DEBUG_UPDATER

#if DEBUG_UPDATER
#warning "Automatic updater debugging is enabled"
#endif

using Bloxstrap.AppData;
using Bloxstrap.Distribution;
using Bloxstrap.Fluxer;
using Bloxstrap.UI.Elements.Bootstrapper.Base;
using ICSharpCode.SharpZipLib.Zip;
using Microsoft.Win32;
using System.ComponentModel;
using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Shell;

namespace Bloxstrap
{
    public class Bootstrapper
    {
        #region Properties
        private const int ProgressBarMaximum = 10000;

        private const double TaskbarProgressMaximumWpf = 1; // this can not be changed. keep it at 1.
        private const int TaskbarProgressMaximumWinForms = WinFormsDialogBase.TaskbarProgressMaximum;

        private const string AppSettings =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n" +
            "<Settings>\r\n" +
            "	<ContentFolder>content</ContentFolder>\r\n" +
            "	<BaseUrl>http://www." + FluxerSite.Domain + "</BaseUrl>\r\n" +
            "</Settings>\r\n";

        private const string ClientSettingsFile = "ClientSettings\\ClientAppSettings.json";

        private const string FpsFlag = "DFIntTaskSchedulerTargetFps";

        // client years whose task scheduler the memory fps unlocker can find
        private static readonly string[] MemoryFpsUnlockYears = { "2019", "2020", "2021" };

        // the flags each fluxer client ships with, which our fast flags get merged on top of
        private const string ClientSettingsBaseFile = "ClientSettings\\ClientAppSettings.json.fluxstrap-base";

        private static readonly JsonDocumentOptions LenientJson = new()
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        };

        private readonly FastZipEvents _fastZipEvents = new();
        private readonly CancellationTokenSource _cancelTokenSource = new();

        private IDistribution Distribution = default!;
        private IAppData AppData = default!;
        private LaunchMode _launchMode;

        private readonly JoinRequest? _joinRequest;
        private readonly bool _invalidLink = false;

        private ClientManifest? _manifest;
        private ClientPackage _package = null!;
        private string _latestVersionDirectory = null!;

        private bool _isInstalling = false;
        private double _progressIncrement;
        private double _taskbarProgressIncrement;
        private double _taskbarProgressMaximum;
        private long _totalDownloadedBytes = 0;
        private long _totalPackagedBytes = 0;
        private bool _packageExtractionSuccess = true;

        private bool _mustUpgrade => App.LaunchSettings.ForceFlag.Active || App.State.Prop.ForceReinstall || !_package.IsInstalled;
        private bool _noConnection = false;

        private AsyncMutex? _mutex;

        private int _appPid = 0;
        private IntPtr _appWindowHandle = IntPtr.Zero;

        public IBootstrapperDialog? Dialog = null;

        public bool IsStudioLaunch => _launchMode != LaunchMode.Player;

        public string MutexName { get; set; } = $"{App.ProjectName}-Bootstrapper";
        public bool QuitIfMutexExists { get; set; } = false;
        #endregion

        #region Core
        public Bootstrapper(LaunchMode launchMode)
        {
            const string LOG_IDENT = "Bootstrapper";

            // version flags don't say which app they're for, so assume the player
            _launchMode = launchMode == LaunchMode.Unknown ? LaunchMode.Player : launchMode;

            string launchArgs = App.LaunchSettings.RobloxLaunchArgs;

            if (!String.IsNullOrEmpty(launchArgs))
            {
                _joinRequest = JoinRequest.Parse(launchArgs);

                if (_joinRequest is null)
                {
                    App.Logger.WriteLine(LOG_IDENT, "Launch link could not be parsed");
                    _invalidLink = true;
                }
                else if (_joinRequest.Studio)
                {
                    App.Logger.WriteLine(LOG_IDENT, "Launch link is for Fluxer Studio");
                    _launchMode = LaunchMode.Studio;
                }
            }

            // https://github.com/icsharpcode/SharpZipLib/blob/master/src/ICSharpCode.SharpZipLib/Zip/FastZip.cs/#L669-L680
            // exceptions don't get thrown if we define events without actually binding to the failure events. probably a bug. ¯\_(ツ)_/¯
            _fastZipEvents.FileFailure += (_, e) =>
            {
                // only give a pass to font files (no idea whats wrong with them)
                if (!e.Name.EndsWith(".ttf"))
                    throw e.Exception;

                App.Logger.WriteLine("FastZipEvents::OnFileFailure", $"Failed to extract {e.Name}");
                _packageExtractionSuccess = false;
            };
            _fastZipEvents.DirectoryFailure += (_, e) => throw e.Exception;
            _fastZipEvents.ProcessFile += (_, e) => e.ContinueRunning = !_cancelTokenSource.IsCancellationRequested;

            SetupData();
        }

        private void SetupData()
        {
            Distribution = App.Distribution;

            AppData = IsStudioLaunch ? Distribution.RobloxStudioData : Distribution.RobloxPlayerData;
        }

        private void SetStatus(string message)
        {
            message = message.Replace("{product}", _package?.Label ?? AppData.ProductName);

            if (Dialog is not null)
                Dialog.Message = message;
        }

        private void UpdateProgressBar()
        {
            if (Dialog is null)
                return;

            // update the download status
            SetStatus(string.Format(
                Strings.Bootstrapper_Status_DownloadingPackages,
                FileSize.ByteSize(_totalDownloadedBytes),
                FileSize.ByteSize(_totalPackagedBytes)
                ));

            // UI progress
            int progressValue = (int)Math.Floor(_progressIncrement * _totalDownloadedBytes);
            progressValue = Math.Clamp(progressValue, 0, ProgressBarMaximum);

            Dialog.ProgressValue = progressValue;

            // taskbar progress
            double taskbarProgressValue = _taskbarProgressIncrement * _totalDownloadedBytes;
            taskbarProgressValue = Math.Clamp(taskbarProgressValue, 0, _taskbarProgressMaximum);

            Dialog.TaskbarProgressValue = taskbarProgressValue;
        }

        private void HandleConnectionError(Exception exception)
        {
            const string LOG_IDENT = "Bootstrapper::HandleConnectionError";

            _noConnection = true;

            App.Logger.WriteLine(LOG_IDENT, "Connectivity check failed");
            App.Logger.WriteException(LOG_IDENT, exception);

            string message = Strings.Dialog_Connectivity_BadConnection;

            if (exception is AggregateException)
                exception = exception.InnerException!;

            // https://gist.github.com/pizzaboxer/4b58303589ee5b14cc64397460a8f386
            if (exception is HttpRequestException && exception.InnerException is null)
                message = String.Format(Strings.Dialog_Connectivity_RobloxDown, $"[{FluxerSite.Domain}]({FluxerSite.Url})");

            bool mustUpgrade = _package is null || _mustUpgrade;

            if (mustUpgrade)
                message += $"\n\n{Strings.Dialog_Connectivity_RobloxUpgradeNeeded}\n\n{Strings.Dialog_Connectivity_TryAgainLater}";
            else
                message += $"\n\n{Strings.Dialog_Connectivity_RobloxUpgradeSkip}";

            Frontend.ShowConnectivityDialog(
                String.Format(Strings.Dialog_Connectivity_UnableToConnect, "Fluxer"),
                message,
                mustUpgrade ? MessageBoxImage.Error : MessageBoxImage.Warning,
                exception);

            if (mustUpgrade)
                App.Terminate(ErrorCode.ERROR_CANCELLED);
        }

        private void ShowError(string message)
        {
            if (!App.LaunchSettings.QuietFlag.Active)
                Frontend.ShowMessageBox(message, MessageBoxImage.Error);
        }

        public async Task Run()
        {
            const string LOG_IDENT = "Bootstrapper::Run";

            App.Logger.WriteLine(LOG_IDENT, "Running bootstrapper");

            // this is now always enabled as of v2.8.0
            if (Dialog is not null)
                Dialog.CancelEnabled = true;

            if (_invalidLink)
            {
                ShowError($"This link is not a Fluxer game link:\n\n{App.LaunchSettings.RobloxLaunchArgs}");
                Dialog?.CloseBootstrapper();
                return;
            }

            SetStatus(Strings.Bootstrapper_Status_Connecting);

            // both are network round trips, so do them at the same time
            var manifestTask = ClientManifest.Fetch(_cancelTokenSource.Token);
            await DnsFix.EnsureResolvable();

            try
            {
                _manifest = await manifestTask;
                App.Logger.WriteLine(LOG_IDENT, $"Got {_manifest.Packages.Count} packages from the client manifest");
            }
            catch (Exception ex)
            {
                // we only know if this is fatal once we know which client is wanted
                _noConnection = true;
                App.Logger.WriteLine(LOG_IDENT, "Could not fetch the client manifest");
                App.Logger.WriteException(LOG_IDENT, ex);

                if (!ResolvePackage(out _))
                {
                    HandleConnectionError(ex);
                    return;
                }
            }

#if (!DEBUG || DEBUG_UPDATER) && !QA_BUILD
            if (App.Settings.Prop.CheckForUpdates && !App.LaunchSettings.UpgradeFlag.Active && !_noConnection)
            {
                bool updatePresent = await CheckForUpdates();

                if (updatePresent)
                    return;
            }
#endif

            // ensure only one instance of the bootstrapper is running at the time
            // so that we don't have stuff like two updates happening simultaneously

            bool mutexExists = Utilities.DoesMutexExist(MutexName);

            if (mutexExists)
            {
                if (!QuitIfMutexExists)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"{MutexName} mutex exists, waiting...");
                    SetStatus(Strings.Bootstrapper_Status_WaitingOtherInstances);
                }
                else
                {
                    App.Logger.WriteLine(LOG_IDENT, $"{MutexName} mutex exists, exiting!");
                    return;
                }
            }

            // wait for mutex to be released if it's not yet
            await using var mutex = new AsyncMutex(false, MutexName);
            await mutex.AcquireAsync(_cancelTokenSource.Token);

            _mutex = mutex;

            // reload our configs since they've likely changed by now
            if (mutexExists)
            {
                App.Settings.Load();
                App.State.Load();
                App.RobloxState.Load();
            }

            if (!ResolvePackage(out string version))
            {
                ShowError($"The {version} client is not available on the server yet.");
                Dialog?.CloseBootstrapper();
                return;
            }

            App.Logger.WriteLine(LOG_IDENT, $"Using package {_package}");

            _latestVersionDirectory = _package.InstallDirectory;

            bool hasTicket = _joinRequest?.HasTicket ?? false;

            if (_package.IsPrivate && !hasTicket && !_package.IsInstalled)
            {
                ShowError($"The Rokori client has to be installed or updated first.\n\nOpen {FluxerSite.Url}, log in and press Play on a game once.");
                Dialog?.CloseBootstrapper();
                return;
            }

            bool allModificationsApplied = true;

            if (_mustUpgrade)
            {
                if (_noConnection)
                {
                    HandleConnectionError(new HttpRequestException("The Fluxer client manifest could not be fetched"));
                    return;
                }

                await InstallClient();
            }

            if (_cancelTokenSource.IsCancellationRequested)
                return;

            AppData.State.VersionGuid = _package.Folder;
            AppData.State.ExecutableName = _package.ExecutableName;
            App.RobloxState.Save();

            allModificationsApplied = await ApplyModifications();

            // check registry entries for every launch, just in case the official launcher changes it back
            WindowsRegistry.RegisterPlayer();
            WindowsRegistry.RegisterClientLocation(IsStudioLaunch, _latestVersionDirectory);

            if (_launchMode != LaunchMode.Player)
                await mutex.ReleaseAsync();

            if (!App.LaunchSettings.NoLaunchFlag.Active && !_cancelTokenSource.IsCancellationRequested)
            {
                if (!App.LaunchSettings.QuietFlag.Active)
                {
                    // show some balloon tips
                    if (!_packageExtractionSuccess)
                        Frontend.ShowBalloonTip(Strings.Bootstrapper_ExtractionFailed_Title, Strings.Bootstrapper_ExtractionFailed_Message, ToolTipIcon.Warning);
                    else if (!allModificationsApplied)
                        Frontend.ShowBalloonTip(Strings.Bootstrapper_ModificationsFailed_Title, Strings.Bootstrapper_ModificationsFailed_Message, ToolTipIcon.Warning);
                }

                if (IsStudioLaunch || hasTicket)
                {
                    StartRoblox();
                }
                else
                {
                    // fluxer games can only be joined with a ticket from the website
                    App.Logger.WriteLine(LOG_IDENT, "No game ticket, sending the user to the website");

                    if (!App.LaunchSettings.QuietFlag.Active)
                        Frontend.ShowMessageBox($"The {_package.Label} client is installed.\n\nLog in on {FluxerSite.Domain}, open a game and press Play.", MessageBoxImage.Information);

                    Utilities.ShellExecute(FluxerSite.GamesUrl);
                }
            }

            await mutex.ReleaseAsync();

            Dialog?.CloseBootstrapper();

            // the 2022 and 2026 clients keep getting patched for a while after starting, which needs us alive
            ClientPatcher.WaitForPatching();
        }

        /// <summary>
        /// Works out which Fluxer client to use, from the manifest or what's already installed
        /// </summary>
        private bool ResolvePackage(out string version)
        {
            const string LOG_IDENT = "Bootstrapper::ResolvePackage";

            if (App.LaunchSettings.VersionFlag.Active && !String.IsNullOrEmpty(App.LaunchSettings.VersionFlag.Data))
                version = App.LaunchSettings.VersionFlag.Data;
            else if (_joinRequest is not null)
                version = _joinRequest.Version;
            else if (IsStudioLaunch)
                version = (App.LaunchSettings.StudioYear ?? App.Settings.Prop.FluxerStudioYear) + "S";
            else
                version = App.Settings.Prop.FluxerPlayerYear + "L";

            App.Logger.WriteLine(LOG_IDENT, $"Wanted client version is {version}");

            ClientPackage? package = null;

            if (_manifest is not null)
            {
                package = _manifest.Find(version);

                // every player year is published as 'L', so fall back to that for unknown variants
                if (package is null && !IsStudioLaunch && version.Length >= 4)
                    package = _manifest.Find(version[..4] + "L");
            }
            else
            {
                package = ClientManifest.FromInstalled(version);
            }

            if (package is null)
                return false;

            _package = package;
            return true;
        }

        private void StartRoblox()
        {
            const string LOG_IDENT = "Bootstrapper::StartRoblox";

            SetStatus(Strings.Bootstrapper_Status_Starting);

            string? logFileName = null;

            string rbxLogDir = Path.Combine(AppData.AppDataDirectory, "logs");
            Directory.CreateDirectory(rbxLogDir);

            var logWatcher = new FileSystemWatcher()
            {
                Path = rbxLogDir,
                // the 2020 client writes log_XXXXX.txt instead of .log files
                Filter = "*.*",
                EnableRaisingEvents = true
            };

            var logCreatedEvent = new AutoResetEvent(false);

            logWatcher.Created += (_, e) =>
            {
                string extension = Path.GetExtension(e.Name ?? "");

                if (!extension.Equals(".log", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
                    return;

                // the crash handler writes its own log, which isn't the one we want
                if (e.Name?.Contains("CrashHandler", StringComparison.OrdinalIgnoreCase) == true)
                    return;

                logWatcher.EnableRaisingEvents = false;
                logFileName = e.FullPath;
                logCreatedEvent.Set();
            };

            var autoclosePids = new List<int>();

            // the code you're gonna read ahead is horrible. sorry for the hack, but it works ¯\_(ツ)_/¯
            // check if prelaunch is checked
            foreach (var integration in App.Settings.Prop.CustomIntegrations)
            {
                if (integration?.PreLaunch == true)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"Pre-Launching custom integration '{integration.Name}' ({integration.Location} {integration.LaunchArgs} - autoclose is {integration.AutoClose})");
                    int pid = 0;
                    try
                    {
                        var process = Process.Start(new ProcessStartInfo
                        {
                            FileName = integration.Location,
                            Arguments = integration.LaunchArgs.Replace("\r\n", " "),
                            WorkingDirectory = Path.GetDirectoryName(integration.Location),
                            UseShellExecute = true
                        })!;
                        pid = process.Id;
                    }
                    catch (Exception ex)
                    {
                        App.Logger.WriteLine(LOG_IDENT, $"Failed to pre-launch integration '{integration.Name}'!");
                        App.Logger.WriteLine(LOG_IDENT, ex.Message);
                    }

                    if (integration?.AutoClose == true && pid != 0)
                        autoclosePids.Add(pid);

                    if (integration?.Delay != null)
                        Thread.Sleep(integration.Delay);
                }
            }

            try
            {
                _appPid = IsStudioLaunch
                    ? ClientLauncher.LaunchStudio(_package, _joinRequest)
                    : ClientLauncher.LaunchPlayer(_package, _joinRequest!);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // 1223 = ERROR_CANCELLED, gets thrown if a UAC prompt is cancelled
                return;
            }
            catch (Exception)
            {
                // attempt a reinstall on next launch
                File.Delete(_package.Md5Path);
                throw;
            }

            if (_appPid == 0)
            {
                ShowError($"The {_package.Label} client could not be started.");
                return;
            }

            try
            {
                using var process = Process.GetProcessById(_appPid);

                var deadline = DateTime.Now.AddSeconds(60);

                while (process.MainWindowHandle == IntPtr.Zero && !process.HasExited && DateTime.Now < deadline)
                {
                    Thread.Sleep(100);
                    process.Refresh();
                }

                _appWindowHandle = process.MainWindowHandle;
            }
            catch (ArgumentException)
            {
                App.Logger.WriteLine(LOG_IDENT, "Client exited straight away");
                return;
            }

            App.Logger.WriteLine(LOG_IDENT, $"Started {_package.Label} (PID {_appPid}), waiting for log file");

            logCreatedEvent.WaitOne(TimeSpan.FromSeconds(15));

            if (String.IsNullOrEmpty(logFileName))
            {
                App.Logger.WriteLine(LOG_IDENT, "Unable to identify log file");
                return;
            }
            else
            {
                App.Logger.WriteLine(LOG_IDENT, $"Got log file as {logFileName}");
            }

            _mutex?.ReleaseAsync();

            if (IsStudioLaunch)
                return;

            // lord.... forgive me for this hack.....
            // launch custom integrations now
            foreach (var integration in App.Settings.Prop.CustomIntegrations)
            {
                if (integration == null)
                    continue;

                if (integration?.PreLaunch == true)
                    continue; // skip pre-launch integrations

                App.Logger.WriteLine(LOG_IDENT, $"Launching custom integration '{integration!.Name}' ({integration.Location} {integration?.LaunchArgs} - autoclose is {integration!.AutoClose})");

                int pid = 0;

                try
                {
                    var process = Process.Start(new ProcessStartInfo
                    {
                        FileName = integration.Location,
                        Arguments = integration.LaunchArgs.Replace("\r\n", " "),
                        WorkingDirectory = Path.GetDirectoryName(integration.Location),
                        UseShellExecute = true
                    })!;

                    pid = process.Id;
                }
                catch (Exception ex)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"Failed to launch integration '{integration.Name}'!");
                    App.Logger.WriteLine(LOG_IDENT, ex.Message);
                }

                if (integration.AutoClose && pid != 0)
                    autoclosePids.Add(pid);
            }

            if (App.Settings.Prop.EnableActivityTracking || App.Settings.Prop.EnableWindowManipulation || App.Settings.Prop.FluxerFpsCap >= 0 || App.LaunchSettings.TestModeFlag.Active || autoclosePids.Any())
            {
                using var ipl = new InterProcessLock("Watcher", TimeSpan.FromSeconds(5));

                var watcherData = new WatcherData
                {
                    ProcessId = _appPid,
                    LogFile = logFileName,
                    AutoclosePids = autoclosePids,
                    Handle = _appWindowHandle.ToInt64()
                };

                string watcherDataArg = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(watcherData)));

                string args = $"-watcher \"{watcherDataArg}\"";

                if (App.LaunchSettings.TestModeFlag.Active)
                    args += " -testmode";

                if (ipl.IsAcquired || true)
                    Process.Start(Paths.Process, args);
            }

            // allow for window to show, since the log is created pretty far beforehand
            Thread.Sleep(1000);
        }

        public void Cancel()
        {
            const string LOG_IDENT = "Bootstrapper::Cancel";

            if (_cancelTokenSource.IsCancellationRequested)
                return;

            App.Logger.WriteLine(LOG_IDENT, "Cancelling launch...");

            _cancelTokenSource.Cancel();

            if (Dialog is not null)
                Dialog.CancelEnabled = false;

            if (_isInstalling)
            {
                try
                {
                    // clean up registry keys
                    WindowsRegistry.RegisterClientLocation(IsStudioLaunch, null);

                    // clean up install
                    if (Directory.Exists(_latestVersionDirectory))
                        Directory.Delete(_latestVersionDirectory, true);
                }
                catch (Exception ex)
                {
                    App.Logger.WriteLine(LOG_IDENT, "Could not fully clean up installation!");
                    App.Logger.WriteException(LOG_IDENT, ex);
                }
            }
            else if (_appPid != 0)
            {
                try
                {
                    using var process = Process.GetProcessById(_appPid);
                    process.Kill();
                }
                catch (Exception) { }
            }

            Dialog?.CloseBootstrapper();

            App.SoftTerminate(ErrorCode.ERROR_CANCELLED);
        }
        #endregion

        #region App Install
        private async Task<bool> CheckForUpdates()
        {
            const string LOG_IDENT = "Bootstrapper::CheckForUpdates";

            // don't update if there's another instance running (likely running in the background)
            // i don't like this, but there isn't much better way of doing it /shrug
            if (Process.GetProcessesByName(App.ProjectName).Length > 1)
            {
                App.Logger.WriteLine(LOG_IDENT, $"More than one {App.ProjectName} instance running, aborting update check");
                return false;
            }

            App.Logger.WriteLine(LOG_IDENT, "Checking for updates...");

#if !DEBUG_UPDATER
            var releaseInfo = await App.GetLatestRelease();

            if (releaseInfo is null)
                return false;

            var versionComparison = Utilities.CompareVersions(App.Version, releaseInfo.TagName);

            // check if we aren't using a deployed build, so we can update to one if a new version comes out
            if (App.IsProductionBuild && versionComparison == VersionComparison.Equal || versionComparison == VersionComparison.GreaterThan)
            {
                App.Logger.WriteLine(LOG_IDENT, "No updates found");
                return false;
            }

            if (Dialog is not null)
                Dialog.CancelEnabled = false;

            string version = releaseInfo.TagName;
#else
            string version = App.Version;
#endif

            SetStatus(Strings.Bootstrapper_Status_UpgradingBloxstrap);

            try
            {
#if DEBUG_UPDATER
                string downloadLocation = Path.Combine(Paths.TempUpdates, $"{App.ProjectName}.exe");

                Directory.CreateDirectory(Paths.TempUpdates);

                File.Copy(Paths.Process, downloadLocation, true);
#else
                var asset = releaseInfo.Assets![0];

                string downloadLocation = Path.Combine(Paths.TempUpdates, asset.Name);

                Directory.CreateDirectory(Paths.TempUpdates);

                App.Logger.WriteLine(LOG_IDENT, $"Downloading {releaseInfo.TagName}...");

                if (!File.Exists(downloadLocation))
                {
                    var response = await App.HttpClient.GetAsync(asset.BrowserDownloadUrl);

                    await using var fileStream = new FileStream(downloadLocation, FileMode.OpenOrCreate, FileAccess.Write);
                    await response.Content.CopyToAsync(fileStream);
                }
#endif

                App.Logger.WriteLine(LOG_IDENT, $"Starting {version}...");

                ProcessStartInfo startInfo = new()
                {
                    FileName = downloadLocation,
                };

                startInfo.ArgumentList.Add("-upgrade");

                foreach (string arg in App.LaunchSettings.Args)
                    startInfo.ArgumentList.Add(arg);

                if (_launchMode == LaunchMode.Player && !startInfo.ArgumentList.Contains("-player"))
                    startInfo.ArgumentList.Add("-player");
                else if (_launchMode == LaunchMode.Studio && !startInfo.ArgumentList.Contains("-studio"))
                    startInfo.ArgumentList.Add("-studio");

                App.Settings.Save();

                new InterProcessLock("AutoUpdater");

                Process.Start(startInfo);

                return true;
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, "An exception occurred when running the auto-updater");
                App.Logger.WriteException(LOG_IDENT, ex);

                Frontend.ShowMessageBox(
                    string.Format(Strings.Bootstrapper_AutoUpdateFailed, version),
                    MessageBoxImage.Information
                );

                Utilities.ShellExecute(App.ProjectDownloadLink);
            }

            return false;
        }
        #endregion

        #region Fluxer Install
        /// <summary>
        /// Kills anything running out of a client folder, so it can be replaced
        /// </summary>
        private static void KillProcessesInDirectory(string directory)
        {
            const string LOG_IDENT = "Bootstrapper::KillProcessesInDirectory";

            string prefix = Path.GetFullPath(directory).TrimEnd('\\') + "\\";

            foreach (var process in Utilities.GetProcessesSafe())
            {
                try
                {
                    if (process.Id == Environment.ProcessId)
                        continue;

                    string? path = process.MainModule?.FileName;

                    if (path is null || !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        continue;

                    App.Logger.WriteLine(LOG_IDENT, $"Closing {process.ProcessName} (PID {process.Id})");

                    process.Kill();
                    process.WaitForExit(3000);
                }
                catch (Exception)
                {
                    // access denied for system processes, we don't care about those
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateHardLink(string newFile, string existingFile, IntPtr securityAttributes);

        /// <summary>
        /// Hard links the files when both folders are on the same drive, which takes about a second instead of
        /// copying a few hundred megabytes. Every write to a client file deletes it first (see PrepareForWrite),
        /// so the official launcher's copy is never changed.
        /// </summary>
        private static void LinkOrCopyDirectory(string source, string destination)
        {
            const string LOG_IDENT = "Bootstrapper::LinkOrCopyDirectory";

            Directory.CreateDirectory(destination);

            foreach (string directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(directory.Replace(source, destination));

            bool canLink = true;
            int linked = 0, copied = 0;

            foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                string target = file.Replace(source, destination);

                if (canLink && CreateHardLink(target, file, IntPtr.Zero))
                {
                    linked++;
                    continue;
                }

                // different drives (or a file system without hard links), so copy everything from here on
                canLink = false;
                File.Copy(file, target, true);
                copied++;
            }

            App.Logger.WriteLine(LOG_IDENT, $"Linked {linked} files, copied {copied}");
        }

        private static long GetDirectorySize(string directory)
        {
            if (!Directory.Exists(directory))
                return 0;

            return Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Sum(x => new FileInfo(x).Length);
        }

        /// <summary>
        /// Reuses a client the official Fluxer launcher already downloaded, if it's the same build
        /// </summary>
        private bool TryImportFromFluxerLauncher()
        {
            const string LOG_IDENT = "Bootstrapper::TryImportFromFluxerLauncher";

            if (!App.Settings.Prop.ImportFluxerLauncherClients)
                return false;

            string source = Path.Combine(FluxerSite.LauncherDirectory, "Versions", _package.Folder);
            string md5Path = Path.Combine(source, ".md5");

            try
            {
                if (!File.Exists(Path.Combine(source, _package.ExecutableName)) || !File.Exists(md5Path))
                    return false;

                if (!File.ReadAllText(md5Path).Trim().Equals(_package.Md5, StringComparison.OrdinalIgnoreCase))
                {
                    App.Logger.WriteLine(LOG_IDENT, $"Fluxer launcher copy of {_package.Folder} is a different build");
                    return false;
                }

                App.Logger.WriteLine(LOG_IDENT, $"Copying {_package.Folder} from the Fluxer launcher...");

                LinkOrCopyDirectory(source, _latestVersionDirectory);

                return true;
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, "Could not copy the client, downloading it instead");
                App.Logger.WriteException(LOG_IDENT, ex);

                return false;
            }
        }

        private async Task InstallClient()
        {
            const string LOG_IDENT = "Bootstrapper::InstallClient";

            if (!Directory.Exists(_latestVersionDirectory))
                SetStatus(Strings.Bootstrapper_Status_Installing);
            else
                SetStatus(Strings.Bootstrapper_Status_Upgrading);

            Directory.CreateDirectory(Paths.Base);
            Directory.CreateDirectory(Paths.Downloads);
            Directory.CreateDirectory(Paths.Versions);

            _isInstalling = true;

            // make sure nothing is running out of the folder before replacing it
            KillProcessesInDirectory(_latestVersionDirectory);

            if (Directory.Exists(_latestVersionDirectory))
            {
                try
                {
                    Directory.Delete(_latestVersionDirectory, true);
                }
                catch (Exception ex)
                {
                    App.Logger.WriteLine(LOG_IDENT, "Failed to delete the old client directory");
                    App.Logger.WriteException(LOG_IDENT, ex);

                    ShowError($"Could not replace the old {_package.Label} files because it's still running.\n\nClose every Fluxer window (also check Task Manager for {_package.ExecutableName}) and try again.");
                    App.Terminate(ErrorCode.ERROR_INSTALL_FAILURE);
                    return;
                }
            }

            if (Dialog is not null)
            {
                Dialog.ProgressStyle = ProgressBarStyle.Marquee;
                Dialog.TaskbarProgressState = TaskbarItemProgressState.Indeterminate;
            }

            bool imported = TryImportFromFluxerLauncher();

            if (!imported)
            {
                await DownloadPackage();

                if (_cancelTokenSource.IsCancellationRequested)
                    return;

                if (Dialog is not null)
                {
                    Dialog.ProgressStyle = ProgressBarStyle.Marquee;
                    Dialog.TaskbarProgressState = TaskbarItemProgressState.Indeterminate;
                    SetStatus(Strings.Bootstrapper_Status_Configuring);
                }

                ExtractPackage();
            }

            if (_cancelTokenSource.IsCancellationRequested)
                return;

            // remember what the client shipped with, so fast flags can be layered on top of it
            string clientSettings = Path.Combine(_latestVersionDirectory, ClientSettingsFile);
            string clientSettingsBase = Path.Combine(_latestVersionDirectory, ClientSettingsBaseFile);

            if (File.Exists(clientSettings))
                File.Copy(clientSettings, clientSettingsBase, true);

            PrepareForWrite(_package.Md5Path);
            await File.WriteAllTextAsync(_package.Md5Path, _package.Md5);

            // a fresh copy has none of our mods in it
            App.RobloxState.Prop.ClientModManifests[_package.Folder] = new();

            CleanupDownloads();

            App.Logger.WriteLine(LOG_IDENT, "Registering approximate program size...");

            int totalSize = (int)(GetDirectorySize(Paths.Versions) / 1024);

            AppData.State.Size = totalSize;

            using (var uninstallKey = Registry.CurrentUser.CreateSubKey(App.UninstallKey))
            {
                uninstallKey.SetValueSafe("EstimatedSize", totalSize);
            }

            App.Logger.WriteLine(LOG_IDENT, $"Registered as {totalSize} KB");

            App.State.Prop.ForceReinstall = false;

            App.State.Save();
            App.RobloxState.Save();

            _isInstalling = false;
        }

        /// <summary>
        /// Deletes downloaded client zips that no installed client uses anymore
        /// </summary>
        private static void CleanupDownloads()
        {
            const string LOG_IDENT = "Bootstrapper::CleanupDownloads";

            if (App.Settings.Prop.DebugDisableVersionPackageCleanup || !Directory.Exists(Paths.Downloads))
                return;

            var installedHashes = Directory.GetDirectories(Paths.Versions)
                .Select(x => Path.Combine(x, ".md5"))
                .Where(File.Exists)
                .Select(x => File.ReadAllText(x).Trim().ToLowerInvariant())
                .ToHashSet();

            foreach (string file in Directory.GetFiles(Paths.Downloads))
            {
                string hash = Path.GetFileName(file);

                if (installedHashes.Contains(hash))
                    continue;

                App.Logger.WriteLine(LOG_IDENT, $"Deleting unused package {hash}");

                try
                {
                    File.Delete(file);
                }
                catch (Exception ex)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"Failed to delete {hash}!");
                    App.Logger.WriteException(LOG_IDENT, ex);
                }
            }
        }

        /// <summary>
        /// Where fishstrap's mods live in older clients
        /// </summary>
        private static readonly Dictionary<string, string> LegacyModPaths = new(StringComparer.OrdinalIgnoreCase)
        {
            { @"content\textures\Cursors\KeyboardMouse\ArrowCursor.png", @"content\textures\ArrowCursor.png" },
            { @"content\textures\Cursors\KeyboardMouse\ArrowFarCursor.png", @"content\textures\ArrowFarCursor.png" },
            { @"content\sounds\ouch.ogg", @"content\sounds\uuhhh.mp3" }
        };

        /// <summary>
        /// Works out where a mod file goes in this client, or null if the client has no use for it.
        /// Each fluxer year lays its content out a bit differently, so mods made for one can miss in another.
        /// </summary>
        private string? ResolveModTarget(string relativeFile)
        {
            if (File.Exists(Path.Combine(_latestVersionDirectory, relativeFile)))
                return relativeFile;

            if (LegacyModPaths.TryGetValue(relativeFile, out string? legacyPath) && File.Exists(Path.Combine(_latestVersionDirectory, legacyPath)))
                return legacyPath;

            // a new file is fine in a folder the client already has, but not in folders it never reads
            string? parent = Path.GetDirectoryName(relativeFile);

            if (String.IsNullOrEmpty(parent) || Directory.Exists(Path.Combine(_latestVersionDirectory, parent)))
                return relativeFile;

            return null;
        }

        private bool CopyModFile(string source, string relativeTarget)
        {
            const string LOG_IDENT = "Bootstrapper::CopyModFile";

            string destination = Path.Combine(_latestVersionDirectory, relativeTarget);

            if (File.Exists(destination) && MD5Hash.FromFile(source) == MD5Hash.FromFile(destination))
                return true;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                PrepareForWrite(destination);
                File.Copy(source, destination);
                Filesystem.AssertReadOnly(destination);

                App.Logger.WriteLine(LOG_IDENT, $"{relativeTarget} has been copied to the version folder");
                return true;
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, $"Failed to apply modification ({relativeTarget})");
                App.Logger.WriteException(LOG_IDENT, ex);
                return false;
            }
        }

        /// <summary>
        /// Clears the way to write a client file. Deleting first (instead of overwriting) matters because clients
        /// imported from the official launcher are hard links, and overwriting would change the launcher's copy too.
        /// </summary>
        private static void PrepareForWrite(string path)
        {
            Filesystem.AssertReadOnly(path);

            if (File.Exists(path))
                File.Delete(path);
        }

        private static JsonObject? ReadJsonObject(string path)
        {
            const string LOG_IDENT = "Bootstrapper::ReadJsonObject";

            if (!File.Exists(path))
                return new JsonObject();

            try
            {
                return JsonNode.Parse(File.ReadAllText(path), documentOptions: LenientJson) as JsonObject;
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, $"Could not parse {path}");
                App.Logger.WriteException(LOG_IDENT, ex);
                return null;
            }
        }

        /// <summary>
        /// Writes the client's own flags with ours layered on top
        /// </summary>
        private bool ApplyFastFlags(string userFlagsPath)
        {
            const string LOG_IDENT = "Bootstrapper::ApplyFastFlags";

            string clientSettings = Path.Combine(_latestVersionDirectory, ClientSettingsFile);
            string clientSettingsBase = Path.Combine(_latestVersionDirectory, ClientSettingsBaseFile);

            // clients installed before we kept a copy: what's there now is still the stock file,
            // unless we've written it ourselves before (clients that don't ship one, like 2022)
            bool writtenBefore = App.RobloxState.Prop.ClientModManifests.GetValueOrDefault(_package.Folder)?.Contains(ClientSettingsFile) == true;

            if (!File.Exists(clientSettingsBase) && File.Exists(clientSettings) && !writtenBefore)
                File.Copy(clientSettings, clientSettingsBase);

            var merged = ReadJsonObject(clientSettingsBase);
            var userFlags = ReadJsonObject(userFlagsPath);

            if (merged is null || userFlags is null)
            {
                App.Logger.WriteLine(LOG_IDENT, "Not touching the client settings, one of them is unreadable");
                return false;
            }

            // the fps unlocker patches 2019-2021 in memory (see Watcher); the others get it through their flag instead
            int fpsCap = App.Settings.Prop.FluxerFpsCap;

            if (fpsCap >= 0 && !_package.IsStudio && !MemoryFpsUnlockYears.Contains(_package.Year) && !userFlags.ContainsKey(FpsFlag))
                userFlags[FpsFlag] = fpsCap == 0 ? 9999 : fpsCap;

            // only hand player clients the flags they actually register (see Fluxer/FlagSupport.cs)
            // flags the client already ships with are always fine to override
            var supportedNames = _package.IsStudio || _package.IsPrivate ? null : FlagSupport.GetNames(_package.Year);
            var skipped = new List<string>();
            int applied = 0;

            // a node can only have one parent, so move each flag across
            foreach (string key in userFlags.Select(x => x.Key).ToList())
            {
                var value = userFlags[key];
                userFlags.Remove(key);

                if (supportedNames is not null && !merged.ContainsKey(key) && !supportedNames.Contains(FlagSupport.StripPrefix(key)))
                {
                    skipped.Add(key);
                    continue;
                }

                merged[key] = value;
                applied++;
            }

            if (skipped.Any())
                App.Logger.WriteLine(LOG_IDENT, $"Not giving {_package.Folder} {skipped.Count} flags it doesn't have: {String.Join(", ", skipped)}");

            // nothing to add, so hand the client its own file untouched
            if (applied == 0)
            {
                if (File.Exists(clientSettingsBase))
                {
                    if (!File.Exists(clientSettings) || MD5Hash.FromFile(clientSettings) != MD5Hash.FromFile(clientSettingsBase))
                    {
                        PrepareForWrite(clientSettings);
                        File.Copy(clientSettingsBase, clientSettings);
                    }
                }
                else if (File.Exists(clientSettings))
                {
                    // the client didn't ship a settings file, so this one is ours from an earlier launch
                    PrepareForWrite(clientSettings);
                }

                return true;
            }

            // old clients' json parsers don't expect characters like + and ' to be \u escaped
            string contents = merged.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });

            if (File.Exists(clientSettings) && File.ReadAllText(clientSettings) == contents)
                return true;

            Directory.CreateDirectory(Path.GetDirectoryName(clientSettings)!);
            PrepareForWrite(clientSettings);
            File.WriteAllText(clientSettings, contents);

            App.Logger.WriteLine(LOG_IDENT, $"Applied {applied} fast flags on top of {_package.Folder}'s own ({skipped.Count} skipped)");

            return true;
        }

        private async Task<bool> ApplyModifications()
        {
            const string LOG_IDENT = "Bootstrapper::ApplyModifications";

            bool success = true;

            SetStatus(Strings.Bootstrapper_Status_ApplyingModifications);

            // handle file mods
            App.Logger.WriteLine(LOG_IDENT, "Checking file mods...");

            // manifest has been moved to State.json
            File.Delete(Path.Combine(Paths.Base, "ModManifest.txt"));

            List<string> modFolderFiles = new();

            Directory.CreateDirectory(Paths.Modifications);

            // check custom font mod
            // instead of replacing the fonts themselves, we'll just alter the font family manifests

            string modFontFamiliesFolder = Path.Combine(Paths.Modifications, "content\\fonts\\families");

            if (File.Exists(Paths.CustomFont))
            {
                App.Logger.WriteLine(LOG_IDENT, "Begin font check");

                Directory.CreateDirectory(modFontFamiliesFolder);

                const string path = "rbxasset://fonts/CustomFont.ttf";

                // older clients don't have font families, in which case there's nothing to rewrite
                string familiesFolder = Path.Combine(_latestVersionDirectory, "content\\fonts\\families");

                if (Directory.Exists(familiesFolder))
                {
                    foreach (string jsonFilePath in Directory.GetFiles(familiesFolder))
                    {
                        string jsonFilename = Path.GetFileName(jsonFilePath);
                        string modFilepath = Path.Combine(modFontFamiliesFolder, jsonFilename);

                        if (File.Exists(modFilepath))
                            continue;

                        App.Logger.WriteLine(LOG_IDENT, $"Setting font for {jsonFilename}");

                        var fontFamilyData = JsonSerializer.Deserialize<FontFamily>(File.ReadAllText(jsonFilePath));

                        if (fontFamilyData is null)
                            continue;

                        bool shouldWrite = false;

                        foreach (var fontFace in fontFamilyData.Faces)
                        {
                            if (fontFace.AssetId != path)
                            {
                                fontFace.AssetId = path;
                                shouldWrite = true;
                            }
                        }

                        if (shouldWrite)
                            File.WriteAllText(modFilepath, JsonSerializer.Serialize(fontFamilyData, new JsonSerializerOptions { WriteIndented = true }));
                    }
                }

                App.Logger.WriteLine(LOG_IDENT, "End font check");
            }
            else if (Directory.Exists(modFontFamiliesFolder))
            {
                Directory.Delete(modFontFamiliesFolder, true);
            }

            // fluxer clients ship their own AppSettings.xml, only write one if it's gone missing
            string appSettingsPath = Path.Combine(_latestVersionDirectory, "AppSettings.xml");

            if (!File.Exists(appSettingsPath) && !File.Exists(Path.Combine(Paths.Modifications, "AppSettings.xml")))
            {
                App.Logger.WriteLine(LOG_IDENT, "Writing AppSettings.xml...");
                await File.WriteAllTextAsync(appSettingsPath, AppSettings);
            }

            foreach (string file in Directory.GetFiles(Paths.Modifications, "*.*", SearchOption.AllDirectories))
            {
                if (_cancelTokenSource.IsCancellationRequested)
                    return true;

                // get relative directory path
                string relativeFile = file.Substring(Paths.Modifications.Length + 1);

                // v1.7.0 - README has been moved to the preferences menu now
                if (relativeFile == "README.txt")
                {
                    File.Delete(file);
                    continue;
                }

                if (relativeFile.EndsWith(".lock"))
                    continue;

                bool isClientSettings = String.Equals(relativeFile, ClientSettingsFile, StringComparison.OrdinalIgnoreCase);

                if (isClientSettings && !App.Settings.Prop.UseFastFlagManager)
                    continue;

                bool isBlacklisted = relativeFile.Contains("content\\avatar\\heads") || relativeFile.Contains("content\\avatar\\compositing") || relativeFile.Contains("content\\avatar\\meshes");

                if (relativeFile.EndsWith(".mesh") && isBlacklisted)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"Skipping file: {relativeFile}");
                    continue;
                }

                modFolderFiles.Add(relativeFile);

                if (isClientSettings)
                {
                    try
                    {
                        if (!ApplyFastFlags(file))
                            success = false;
                    }
                    catch (Exception ex)
                    {
                        App.Logger.WriteLine(LOG_IDENT, "Failed to apply fast flags");
                        App.Logger.WriteException(LOG_IDENT, ex);
                        success = false;
                    }

                    continue;
                }

                string? target = ResolveModTarget(relativeFile);

                if (target is null)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"{_package.Folder} has no use for {relativeFile}, skipping it");
                    modFolderFiles.Remove(relativeFile);
                    continue;
                }

                if (target != relativeFile)
                {
                    modFolderFiles.Remove(relativeFile);
                    modFolderFiles.Add(target);
                }

                if (!CopyModFile(file, target))
                    success = false;
            }

            // the fps unlocker can need the client settings written even with no fast flags of the user's own
            if (!modFolderFiles.Contains(ClientSettingsFile) && App.Settings.Prop.FluxerFpsCap >= 0)
            {
                modFolderFiles.Add(ClientSettingsFile);

                if (!ApplyFastFlags(Path.Combine(Paths.Modifications, ClientSettingsFile)))
                    success = false;
            }

            // older clients have no font families to point at the custom font, so swap the font files themselves
            if (File.Exists(Paths.CustomFont) && !Directory.Exists(Path.Combine(_latestVersionDirectory, "content\\fonts\\families")))
            {
                string fontsFolder = Path.Combine(_latestVersionDirectory, "content\\fonts");

                if (Directory.Exists(fontsFolder))
                {
                    foreach (string fontFile in Directory.GetFiles(fontsFolder))
                    {
                        string extension = Path.GetExtension(fontFile);
                        string name = Path.GetFileName(fontFile);

                        if (!extension.Equals(".ttf", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".otf", StringComparison.OrdinalIgnoreCase))
                            continue;

                        // emoji need their own font, and the custom font itself is already in place
                        if (name.Contains("emoji", StringComparison.OrdinalIgnoreCase) || name.Equals("CustomFont.ttf", StringComparison.OrdinalIgnoreCase))
                            continue;

                        string relativeFont = Path.Combine("content\\fonts", name);

                        // a mod that replaces this exact font wins
                        if (modFolderFiles.Contains(relativeFont))
                            continue;

                        modFolderFiles.Add(relativeFont);

                        if (!CopyModFile(Paths.CustomFont, relativeFont))
                            success = false;
                    }
                }
            }

            // the manifest is here to keep track of what files have been deleted from the modifications folder,
            // so that we know when to restore the original files from the downloaded client
            // each client folder has its own, since mods only get applied to the client being launched

            var manifests = App.RobloxState.Prop.ClientModManifests;
            var previousFiles = manifests.GetValueOrDefault(_package.Folder) ?? new();
            var filesToRestore = new List<string>();

            foreach (string fileLocation in previousFiles)
            {
                if (modFolderFiles.Contains(fileLocation))
                    continue;

                if (String.Equals(fileLocation, ClientSettingsFile, StringComparison.OrdinalIgnoreCase))
                {
                    string clientSettingsBase = Path.Combine(_latestVersionDirectory, ClientSettingsBaseFile);
                    string clientSettings = Path.Combine(_latestVersionDirectory, ClientSettingsFile);

                    App.Logger.WriteLine(LOG_IDENT, "Fast flags were removed, restoring the client's own");

                    PrepareForWrite(clientSettings);

                    if (File.Exists(clientSettingsBase))
                        File.Copy(clientSettingsBase, clientSettings);

                    continue;
                }

                App.Logger.WriteLine(LOG_IDENT, $"{fileLocation} was removed as a mod, restoring it");
                filesToRestore.Add(fileLocation);
            }

            if (filesToRestore.Any())
            {
                var unrestored = await RestoreFiles(filesToRestore);

                // keep tracking anything we couldn't restore, so we try again next launch
                modFolderFiles.AddRange(unrestored);
            }

            // make sure we're not overwriting a new update
            // if we're the background update process, always overwrite
            if (App.LaunchSettings.BackgroundUpdaterFlag.Active || !App.RobloxState.HasFileOnDiskChanged())
            {
                manifests[_package.Folder] = modFolderFiles;
                App.RobloxState.Save();
            }
            else
            {
                App.Logger.WriteLine(LOG_IDENT, "RobloxState disk mismatch, not saving ModManifest");
            }

            App.Logger.WriteLine(LOG_IDENT, $"Finished checking file mods");

            if (!success)
                App.Logger.WriteLine(LOG_IDENT, "Failed to apply all modifications");

            return success;
        }

        /// <summary>
        /// Puts the original files back from the client's zip. Returns the files that couldn't be restored.
        /// </summary>
        private bool TryRestoreFromFluxerLauncher(List<string> files)
        {
            const string LOG_IDENT = "Bootstrapper::TryRestoreFromFluxerLauncher";

            string source = Path.Combine(FluxerSite.LauncherDirectory, "Versions", _package.Folder);
            string md5Path = Path.Combine(source, ".md5");

            try
            {
                if (!File.Exists(md5Path) || !File.ReadAllText(md5Path).Trim().Equals(_package.Md5, StringComparison.OrdinalIgnoreCase))
                    return false;

                foreach (string file in files)
                {
                    string original = Path.Combine(source, file);
                    string versionFile = Path.Combine(_latestVersionDirectory, file);

                    PrepareForWrite(versionFile);

                    if (File.Exists(original))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(versionFile)!);
                        File.Copy(original, versionFile);
                        App.Logger.WriteLine(LOG_IDENT, $"Restored {file}");
                    }
                    else
                    {
                        // wasn't part of the client to begin with
                        App.Logger.WriteLine(LOG_IDENT, $"Deleted {file}");
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, "Could not restore from the Fluxer launcher, falling back to the client zip");
                App.Logger.WriteException(LOG_IDENT, ex);
                return false;
            }
        }

        private async Task<List<string>> RestoreFiles(List<string> files)
        {
            const string LOG_IDENT = "Bootstrapper::RestoreFiles";

            // the official launcher's copy of the same build has the originals, which saves a download
            if (!File.Exists(_package.DownloadPath) && TryRestoreFromFluxerLauncher(files))
                return new();

            if (!File.Exists(_package.DownloadPath))
            {
                if (_noConnection || String.IsNullOrEmpty(_package.ZipName))
                {
                    App.Logger.WriteLine(LOG_IDENT, "Client zip isn't available, restoring later");
                    return files;
                }

                await DownloadPackage();
            }

            if (!File.Exists(_package.DownloadPath))
                return files;

            using var archive = System.IO.Compression.ZipFile.OpenRead(_package.DownloadPath);

            var entries = archive.Entries.ToDictionary(x => x.FullName.Replace('/', '\\'), StringComparer.OrdinalIgnoreCase);

            foreach (string file in files)
            {
                string versionFile = Path.Combine(_latestVersionDirectory, file);

                try
                {
                    PrepareForWrite(versionFile);

                    if (entries.TryGetValue(file, out var entry))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(versionFile)!);
                        entry.ExtractToFile(versionFile);
                        App.Logger.WriteLine(LOG_IDENT, $"Restored {file}");
                    }
                    else
                    {
                        // wasn't part of the client to begin with
                        App.Logger.WriteLine(LOG_IDENT, $"Deleted {file}");
                    }
                }
                catch (Exception ex)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"Failed to restore {file}");
                    App.Logger.WriteException(LOG_IDENT, ex);
                }
            }

            return new();
        }

        private async Task DownloadPackage()
        {
            string LOG_IDENT = $"Bootstrapper::DownloadPackage.{_package.Folder}";

            if (_cancelTokenSource.IsCancellationRequested)
                return;

            Directory.CreateDirectory(Paths.Downloads);

            string packageUrl = (_package.IsPrivate ? FluxerSite.PrivateSetupUrl : FluxerSite.SetupUrl) + _package.ZipName;

            if (File.Exists(_package.DownloadPath))
            {
                string calculatedMD5 = MD5Hash.FromFile(_package.DownloadPath);

                if (calculatedMD5 != _package.Md5)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"Package is corrupted ({calculatedMD5} != {_package.Md5})! Deleting and re-downloading...");
                    File.Delete(_package.DownloadPath);
                }
                else
                {
                    App.Logger.WriteLine(LOG_IDENT, $"Package is already downloaded, skipping...");
                    return;
                }
            }

            const int maxTries = 5;

            App.Logger.WriteLine(LOG_IDENT, $"Downloading {packageUrl}...");

            var buffer = new byte[81920];

            for (int i = 1; i <= maxTries; i++)
            {
                if (_cancelTokenSource.IsCancellationRequested)
                    return;

                long totalBytesRead = 0;

                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, packageUrl);

                    // the private client is only handed out to logged in users
                    if (_package.IsPrivate && !String.IsNullOrEmpty(_joinRequest?.Ticket))
                        request.Headers.Add("Cookie", $"{FluxerSite.SecurityCookie}={_joinRequest.Ticket}");

                    using var response = await App.HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _cancelTokenSource.Token);
                    response.EnsureSuccessStatusCode();

                    _totalPackagedBytes = response.Content.Headers.ContentLength ?? 0;
                    _totalDownloadedBytes = 0;

                    if (Dialog is not null && _totalPackagedBytes > 0)
                    {
                        Dialog.ProgressStyle = ProgressBarStyle.Continuous;
                        Dialog.TaskbarProgressState = TaskbarItemProgressState.Normal;
                        Dialog.ProgressMaximum = ProgressBarMaximum;

                        _progressIncrement = (double)ProgressBarMaximum / _totalPackagedBytes;

                        if (Dialog is WinFormsDialogBase)
                            _taskbarProgressMaximum = (double)TaskbarProgressMaximumWinForms;
                        else
                            _taskbarProgressMaximum = (double)TaskbarProgressMaximumWpf;

                        _taskbarProgressIncrement = _taskbarProgressMaximum / (double)_totalPackagedBytes;
                    }

                    if (_totalPackagedBytes > 0 && Filesystem.GetFreeDiskSpace(Paths.Base) < _totalPackagedBytes * 3)
                    {
                        Frontend.ShowMessageBox(Strings.Bootstrapper_NotEnoughSpace, MessageBoxImage.Error);
                        App.Terminate(ErrorCode.ERROR_INSTALL_FAILURE);
                        return;
                    }

                    await using var stream = await response.Content.ReadAsStreamAsync(_cancelTokenSource.Token);
                    await using var fileStream = new FileStream(_package.DownloadPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Delete);

                    while (true)
                    {
                        if (_cancelTokenSource.IsCancellationRequested)
                        {
                            stream.Close();
                            fileStream.Close();
                            return;
                        }

                        int bytesRead = await stream.ReadAsync(buffer, _cancelTokenSource.Token);

                        if (bytesRead == 0)
                            break;

                        totalBytesRead += bytesRead;

                        await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), _cancelTokenSource.Token);

                        _totalDownloadedBytes += bytesRead;
                        UpdateProgressBar();
                    }

                    string hash = MD5Hash.FromStream(fileStream);

                    if (hash != _package.Md5)
                        throw new ChecksumFailedException($"Failed to verify download of {packageUrl}\n\nExpected hash: {_package.Md5}\nGot hash: {hash}");

                    App.Logger.WriteLine(LOG_IDENT, $"Finished downloading! ({totalBytesRead} bytes total)");
                    break;
                }
                catch (Exception ex)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"An exception occurred after downloading {totalBytesRead} bytes. ({i}/{maxTries})");
                    App.Logger.WriteException(LOG_IDENT, ex);

                    if (File.Exists(_package.DownloadPath))
                        File.Delete(_package.DownloadPath);

                    if (ex.GetType() == typeof(ChecksumFailedException))
                    {
                        Frontend.ShowConnectivityDialog(
                            Strings.Dialog_Connectivity_UnableToDownload,
                            String.Format(Strings.Dialog_Connectivity_UnableToDownloadReason, $"[{FluxerSite.Url}]({FluxerSite.Url})"),
                            MessageBoxImage.Error,
                            ex
                        );

                        App.Terminate(ErrorCode.ERROR_CANCELLED);
                    }
                    else if (i >= maxTries)
                        throw;

                    _totalDownloadedBytes = 0;
                    UpdateProgressBar();
                }
            }
        }

        private void ExtractPackage()
        {
            const string LOG_IDENT = "Bootstrapper::ExtractPackage";

            App.Logger.WriteLine(LOG_IDENT, $"Extracting {_package.ZipName}...");

            Directory.CreateDirectory(_latestVersionDirectory);

            var fastZip = new FastZip(_fastZipEvents);

            fastZip.ExtractZip(_package.DownloadPath, _latestVersionDirectory, null);

            App.Logger.WriteLine(LOG_IDENT, $"Finished extracting {_package.ZipName}");
        }
        #endregion
    }
}
