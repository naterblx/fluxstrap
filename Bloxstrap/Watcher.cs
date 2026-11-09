using Bloxstrap.AppData;
using Bloxstrap.Fluxer;
using Bloxstrap.Integrations;

namespace Bloxstrap
{
    public class Watcher : IDisposable
    {
        private readonly InterProcessLock _lock = new("Watcher");

        private readonly WatcherData? _watcherData;
        
        private readonly NotifyIconWrapper? _notifyIcon;

        public readonly ActivityWatcher? ActivityWatcher;

        public readonly WindowManipulation? WindowManipulation;

        public readonly DiscordRichPresence? RichPresence;

        public readonly VoiceChat? VoiceChat;

#if DEV_BUILD
        private readonly UI.Elements.Overlay.VoiceMuteButton? _muteButton;
#endif

        private readonly Dictionary<int, FpsUnlocker> _fpsUnlockers = new();

        public Watcher()
        {
            const string LOG_IDENT = "Watcher";


            if (!_lock.IsAcquired)
            {
                App.Logger.WriteLine(LOG_IDENT, "Watcher instance already exists");
                return;
            }

            string? watcherDataArg = App.LaunchSettings.WatcherFlag.Data;

            if (String.IsNullOrEmpty(watcherDataArg))
            {
#if DEBUG
                string path = new RobloxPlayerData().ExecutablePath;
                if (!File.Exists(path))
                    throw new ApplicationException("Roblox player is not been installed");

                using var gameClientProcess = Process.Start(path);

                while (gameClientProcess.MainWindowHandle == IntPtr.Zero)
                    Thread.Sleep(100);

                _watcherData = new() { ProcessId = gameClientProcess.Id, Handle = gameClientProcess.MainWindowHandle.ToInt64() };
#else
                throw new Exception("Watcher data not specified");
#endif
            }
            else
            {
                _watcherData = JsonSerializer.Deserialize<WatcherData>(Encoding.UTF8.GetString(Convert.FromBase64String(watcherDataArg)));
            }

            if (_watcherData is null)
                throw new Exception("Watcher data is invalid");

            if (App.Settings.Prop.EnableWindowManipulation && _watcherData.Handle != 0)
                WindowManipulation = new(_watcherData.Handle, _watcherData.ProcessId);

            if (App.Settings.Prop.EnableActivityTracking)
            {
                ActivityWatcher = new(_watcherData.LogFile);

                if (App.Settings.Prop.UseDisableAppPatch)
                {
                    ActivityWatcher.OnAppClose += delegate
                    {
                        App.Logger.WriteLine(LOG_IDENT, "Received desktop app exit, closing Roblox");
                        using var process = Process.GetProcessById(_watcherData.ProcessId);
                        process.CloseMainWindow();
                    };
                }

                if (App.Settings.Prop.UseDiscordRichPresence && !App.State.Prop.WatcherRunning)
                {
                    App.Logger.WriteLine(LOG_IDENT, "Running rpc");
                    RichPresence = new(ActivityWatcher);
                }

                if (App.Settings.Prop.VoiceChatEnabled)
                {
                    VoiceChat = new(ActivityWatcher, _watcherData.ProcessId);
#if DEV_BUILD
                    _muteButton = new(VoiceChat, _watcherData.ProcessId);
#endif
                }
            }

            _notifyIcon = new(this);
        }

        public void KillRobloxProcess() => CloseProcess(_watcherData!.ProcessId, true);

        public void CloseProcess(int pid, bool force = false)
        {
            const string LOG_IDENT = "Watcher::CloseProcess";

            try
            {
                using var process = Process.GetProcessById(pid);

                App.Logger.WriteLine(LOG_IDENT, $"Killing process '{process.ProcessName}' (pid={pid}, force={force})");

                if (process.HasExited)
                {
                    App.Logger.WriteLine(LOG_IDENT, $"PID {pid} has already exited");
                    return;
                }

                if (force)
                    process.Kill();
                else
                    process.CloseMainWindow();
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, $"PID {pid} could not be closed");
                App.Logger.WriteException(LOG_IDENT, ex);
            }
        }

        public async Task Run()
        {
            if (!_lock.IsAcquired || _watcherData is null)
                return;

            ActivityWatcher?.Start();
            WindowManipulation?.Start();

            int ticks = 0;

            while (Utilities.GetProcessesSafe().Any(x => x.Id == _watcherData.ProcessId))
            {
                // the official launcher re-applies the cap every couple of seconds too, since the client can reset it
                if (ticks++ % 2 == 0)
                    UpdateFpsUnlockers();

                await Task.Delay(1000);
            }

            foreach (var unlocker in _fpsUnlockers.Values)
                unlocker.Close();

            if (_watcherData.AutoclosePids is not null)
            {
                foreach (int pid in _watcherData.AutoclosePids)
                    CloseProcess(pid);
            }

            if (App.LaunchSettings.TestModeFlag.Active)
                Process.Start(Paths.Process, "-settings -testmode");
        }

        /// <summary>
        /// Keeps every fluxer client launched from our folder at the chosen fps cap, including extra instances
        /// </summary>
        private void UpdateFpsUnlockers()
        {
            const string LOG_IDENT = "Watcher::UpdateFpsUnlockers";

            int cap = App.Settings.Prop.FluxerFpsCap;

            if (cap < 0)
                return;

            string versionsPrefix = Paths.Versions.TrimEnd('\\') + "\\";

            foreach (string name in new[] { App.FluxerPlayerAppName, App.RobloxPlayerAppName })
            {
                foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(name)))
                {
                    try
                    {
                        if (!_fpsUnlockers.ContainsKey(process.Id) && process.MainModule?.FileName?.StartsWith(versionsPrefix, StringComparison.OrdinalIgnoreCase) == true)
                            _fpsUnlockers[process.Id] = new FpsUnlocker(process);
                    }
                    catch (Exception) { }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }

            foreach (int pid in _fpsUnlockers.Where(x => x.Value.Dead).Select(x => x.Key).ToList())
            {
                _fpsUnlockers[pid].Close();
                _fpsUnlockers.Remove(pid);
            }

            foreach (var (pid, unlocker) in _fpsUnlockers)
            {
                bool wasReady = unlocker.Ready;

                unlocker.Tick();

                if (!unlocker.Ready)
                    continue;

                unlocker.SetCap(cap);

                if (!wasReady)
                    App.Logger.WriteLine(LOG_IDENT, $"Unlocked fps for PID {pid} (cap {(cap == 0 ? "unlimited" : cap)})");
            }
        }

        public void Dispose()
        {
            App.Logger.WriteLine("Watcher::Dispose", "Disposing Watcher");

            _notifyIcon?.Dispose();
            RichPresence?.Dispose();
            VoiceChat?.Dispose();
#if DEV_BUILD
            _muteButton?.Dispatcher.Invoke(_muteButton.Close);
#endif

            App.State.Prop.WatcherRunning = false;

            GC.SuppressFinalize(this);
        }
    }
}
