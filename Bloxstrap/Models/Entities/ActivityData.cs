using System.DirectoryServices.ActiveDirectory;
using System.Runtime.InteropServices;
using System.Web;
using System.Windows;
using System.Windows.Input;
using Bloxstrap.AppData;
using Bloxstrap.Models.APIs.RoValra;
using CommunityToolkit.Mvvm.Input;

namespace Bloxstrap.Models.Entities
{
    public class ActivityData
    {

        private long _universeId = 0;

        /// <summary>
        /// If the current activity stems from an in-universe teleport, then this will be
        /// set to the activity that corresponds to the initial game join
        /// </summary>
        public ActivityData? RootActivity;

        public long UniverseId
        {
            get => _universeId;
            set
            {
                _universeId = value;
                UniverseDetails.LoadFromCache(value);
            }
        }

        public long PlaceId { get; set; } = 0;

        public string JobId { get; set; } = string.Empty;

        public DateTime? StartTime { get; set; }

        /// <summary>
        /// This will be empty unless the server joined is a private server
        /// </summary>
        public string AccessCode { get; set; } = string.Empty;
        
        public long UserId { get; set; } = 0;

        public string MachineAddress { get; set; } = string.Empty;

        public bool MachineAddressValid => !string.IsNullOrEmpty(MachineAddress) && !MachineAddress.StartsWith("10.");

        public bool IsTeleport { get; set; } = false;

        public ServerType ServerType { get; set; } = ServerType.Public;

        public DateTime TimeJoined { get; set; }

        public DateTime? TimeLeft { get; set; }

        // everything below here is optional strictly for bloxstraprpc, discord rich presence, or game history

        /// <summary>
        /// This is intended only for other people to use, i.e. context menu invite link, rich presence joining
        /// </summary>
        public string RPCLaunchData { get; set; } = string.Empty;

        public UniverseDetails? UniverseDetails { get; set; }

        public string GameHistoryDescription
        {
            get
            {
                string desc = string.Format(
                    "{0} • {1} {2} {3}", 
                    UniverseDetails?.Data.Creator.Name,
                    TimeJoined.ToString("t"), 
                    Locale.CurrentCulture.Name.StartsWith("ja") ? '~' : '-',
                    TimeLeft?.ToString("t")
                );

                if (ServerType != ServerType.Public)
                    desc += " • " + ServerType.ToTranslatedString();

                return desc;
            }
        }

        public ICommand RejoinServerCommand => new RelayCommand(RejoinServer);

        private SemaphoreSlim serverQuerySemaphore = new(1, 1);

        public string GetInviteDeeplink(bool launchData = true, bool useRobloxUri = false)
        {
            // if our data isnt loaded it uses dummy data
            // we only wait for important data

            // fluxer games are joined from their page on the site, which hands out the ticket
            return Fluxer.FluxerSite.GetGamePageUrl(PlaceId);
        }

        public async Task<string?> QueryServerLocation()
        {
            const string LOG_IDENT = "ActivityData::QueryServerLocation";

            if (!MachineAddressValid)
                throw new InvalidOperationException($"Machine address is invalid ({MachineAddress})");

            await serverQuerySemaphore.WaitAsync();

            if (GlobalCache.ServerLocation.TryGetValue(MachineAddress, out string? location))
            {
                serverQuerySemaphore.Release();
                return location;
            }

            try
            {
                // rovalra only knows roblox's datacenters, fluxer servers are hosted elsewhere
                Uri ipInfoUrl = new($"https://ipinfo.io/{MachineAddress}/json");
                var geolocation = await Http.GetJson<Models.APIs.IPInfoResponse>(ipInfoUrl);

                if (geolocation is null || String.IsNullOrEmpty(geolocation.Country))
                    location = Strings.Common_Unknown;
                else
                {
                    if (geolocation.City == geolocation.Region && geolocation.City == geolocation.Country)
                        location = geolocation.Country;
                    else if (geolocation.City == geolocation.Region)
                        location = $"{geolocation.Region}, {geolocation.Country}";
                    else
                        location = $"{geolocation.City}, {geolocation.Region}, {geolocation.Country}";
                }

                GlobalCache.ServerLocation[MachineAddress] = location;
                serverQuerySemaphore.Release();
            }
            catch (Exception ex)
            {
                App.Logger.WriteLine(LOG_IDENT, $"Failed to get server location for {MachineAddress}");
                App.Logger.WriteException(LOG_IDENT, ex);

                GlobalCache.ServerLocation[MachineAddress] = location;
                serverQuerySemaphore.Release();

                Frontend.ShowConnectivityDialog(
                    string.Format(Strings.Dialog_Connectivity_UnableToConnect, "ipinfo.io"),
                    Strings.ActivityWatcher_LocationQueryFailed,
                    MessageBoxImage.Warning,
                    ex
                );
            }

            return location;
        }

        public override string ToString() => $"{PlaceId}/{JobId}";

        private void RejoinServer()
        {
            // a ticket is needed to join, so send the user to the game's page to press play again
            Utilities.ShellExecute(GetInviteDeeplink(false));
        }
    }
}
