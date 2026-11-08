using System.Windows.Input;
using Bloxstrap.Fluxer;
using CommunityToolkit.Mvvm.Input;

namespace Bloxstrap.UI.ViewModels.Settings
{
    public class FluxerClientEntry
    {
        public string Label { get; init; } = "";

        public string Folder { get; init; } = "";

        public string Status { get; init; } = "";
    }

    /// <summary>
    /// Backs the Fluxer page (which replaced fishstrap's roblox channel page)
    /// </summary>
    public class ChannelViewModel : NotifyPropertyChangedViewModel
    {
        public bool IsRobloxInstallationMissing => String.IsNullOrEmpty(App.RobloxState.Prop.Player.VersionGuid) && String.IsNullOrEmpty(App.RobloxState.Prop.Studio.VersionGuid);

        public ChannelViewModel()
        {
            Task.Run(LoadClients);
        }

        private async Task LoadClients()
        {
            const string LOG_IDENT = "ChannelViewModel::LoadClients";

            ClientsLoadingText = "Fetching the Fluxer client list...";
            OnPropertyChanged(nameof(ClientsLoadingText));

            try
            {
                var manifest = await ClientManifest.Fetch();

                PlayerYears = manifest.GetDistinctFolders(false).Select(x => x.Year).ToList();
                StudioYears = manifest.GetDistinctFolders(true).Select(x => x.Year).ToList();

                Clients = manifest.GetDistinctFolders(false)
                    .Concat(manifest.GetDistinctFolders(true))
                    .Concat(manifest.Packages.Where(x => x.IsPrivate).Take(1))
                    .Select(x => new FluxerClientEntry
                    {
                        Label = x.Label,
                        Folder = x.Folder,
                        Status = GetStatus(x)
                    })
                    .ToList();

                ClientsLoadingText = "";
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, "Could not fetch the client manifest");
                App.Logger.WriteException(LOG_IDENT, ex);

                ClientsLoadingText = $"Could not reach {FluxerSite.Domain} ({ex.Message})";
            }

            OnPropertyChanged(nameof(PlayerYears));
            OnPropertyChanged(nameof(StudioYears));
            OnPropertyChanged(nameof(Clients));
            OnPropertyChanged(nameof(ClientsLoadingText));
            OnPropertyChanged(nameof(SelectedPlayerYear));
            OnPropertyChanged(nameof(SelectedStudioYear));
        }

        public ICommand InstallAllClientsCommand => new RelayCommand(() => Task.Run(InstallAllClients));

        public bool CanInstallAllClients { get; private set; } = true;

        /// <summary>
        /// Gets every player client ready ahead of time, so joining a game never waits on a download
        /// </summary>
        private async Task InstallAllClients()
        {
            const string LOG_IDENT = "ChannelViewModel::InstallAllClients";

            CanInstallAllClients = false;
            OnPropertyChanged(nameof(CanInstallAllClients));

            try
            {
                var manifest = await ClientManifest.Fetch();
                var missing = manifest.GetDistinctFolders(false).Where(x => !x.IsInstalled).ToList();

                for (int i = 0; i < missing.Count; i++)
                {
                    ClientsLoadingText = $"Installing {missing[i].Label} ({i + 1}/{missing.Count})...";
                    OnPropertyChanged(nameof(ClientsLoadingText));

                    // the bootstrapper does the install; -nolaunch stops it from opening the website afterwards
                    using var process = Process.Start(Paths.Application, $"-player -nolaunch -quiet -version {missing[i].Version}");
                    await process.WaitForExitAsync();
                }
            }
            catch (Exception ex)
            {
                App.Logger.WriteException(LOG_IDENT, ex);
            }

            CanInstallAllClients = true;
            OnPropertyChanged(nameof(CanInstallAllClients));

            await LoadClients();
        }

        private static string GetStatus(ClientPackage package)
        {
            if (package.IsInstalled)
                return "Installed";

            if (File.Exists(package.Md5Path))
                return "Update available";

            return "Not installed (downloaded when first launched)";
        }

        public string ClientsLoadingText { get; private set; } = "";

        public List<FluxerClientEntry> Clients { get; private set; } = new();

        // shown until the manifest loads
        public List<string> PlayerYears { get; private set; } = new() { App.Settings.Prop.FluxerPlayerYear };

        public List<string> StudioYears { get; private set; } = new() { App.Settings.Prop.FluxerStudioYear };

        public string SelectedPlayerYear
        {
            get => App.Settings.Prop.FluxerPlayerYear;
            set
            {
                if (!String.IsNullOrEmpty(value))
                    App.Settings.Prop.FluxerPlayerYear = value;
            }
        }

        public string SelectedStudioYear
        {
            get => App.Settings.Prop.FluxerStudioYear;
            set
            {
                if (!String.IsNullOrEmpty(value))
                    App.Settings.Prop.FluxerStudioYear = value;
            }
        }

        public bool ImportFluxerLauncherClients
        {
            get => App.Settings.Prop.ImportFluxerLauncherClients;
            set => App.Settings.Prop.ImportFluxerLauncherClients = value;
        }

        public bool UpdateCheckingEnabled
        {
            get => App.Settings.Prop.CheckForUpdates;
            set => App.Settings.Prop.CheckForUpdates = value;
        }

        public bool ForceRobloxReinstallation
        {
            get => App.State.Prop.ForceReinstall || IsRobloxInstallationMissing;
            set => App.State.Prop.ForceReinstall = value;
        }
    }
}
