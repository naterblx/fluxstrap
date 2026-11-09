using DiscordRPC;
using System.Collections.ObjectModel;

namespace Bloxstrap.Models.Persistable
{
    public class Settings
    {
        // uh
        public bool AllowCookieAccess { get; set; } = false;

        // bloxstrap configuration
        public BootstrapperStyle BootstrapperStyle { get; set; } = BootstrapperStyle.FluentAeroDialog;
        public BootstrapperIcon BootstrapperIcon { get; set; } = BootstrapperIcon.IconBloxstrap;
        public string BootstrapperTitle { get; set; } = App.ProjectName;
        public string BootstrapperIconCustomLocation { get; set; } = "";
        public RobloxIcon RobloxIcon { get; set; } = RobloxIcon.IconDefault;
        public string RobloxTitle { get; set; } = "Fluxer";
        public string RobloxIconCustomLocation { get; set; } = "";
        public Theme Theme { get; set; } = Theme.Default;
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool DeveloperMode { get; set; } = false;
        // PLEASE DONT FORGET TO TURN THIS OFF !!!!
        public bool UseAcrylicBackground { get; set; } = false;
        public byte AcrylicBackgroundOpacity { get; set; } = 165;
        public bool ForceLocalData { get; set; } = false;
        public bool CheckForUpdates { get; set; } = true;
        public bool ConfirmLaunches { get; set; } = true;
        public bool EnableRobloxBackgroundApp { get; set; } = false;
        public RobloxTheme RobloxTheme { get; set; } = RobloxTheme.Default;
        public string Locale { get; set; } = "nil";
        public bool ForceRobloxLanguage { get; set; } = false;
        public bool UseFastFlagManager { get; set; } = true;
        public bool WPFSoftwareRender { get; set; } = false;
        public bool EnableAnalytics { get; set; } = false;
        public bool StaticDirectory { get; set; } = false;
        public string Channel { get; set; } = RobloxInterfaces.Deployment.DefaultChannel;
        public string RobloxDomain { get; set; } = Fluxer.FluxerSite.Domain;
        public ChannelChangeMode ChannelChangeMode { get; set; } = ChannelChangeMode.Automatic;
        public DistributorType DistributorType { get; set; } = DistributorType.Global;
        public string? SelectedCustomTheme { get; set; } = null;
        public bool BackgroundUpdatesEnabled { get; set; } = false;
        public bool DebugDisableVersionPackageCleanup { get; set; } = false;
        public bool EnableBetterMatchmaking { get; set; } = false;
        public bool EnableBetterMatchmakingRandomization { get; set; } = false;
        public WebEnvironment WebEnvironment { get; set; } = WebEnvironment.Production;

        // fluxer configuration
        public string FluxerPlayerYear { get; set; } = "2021";
        public string FluxerStudioYear { get; set; } = "2021";
        public bool ImportFluxerLauncherClients { get; set; } = true;
        // fps cap applied by patching the client's task scheduler (see Fluxer/FpsUnlocker.cs)
        // -1 leaves the client alone, 0 is unlimited
        public int FluxerFpsCap { get; set; } = -1;

        // integration configuration
        public CleanerOptions CleanerOptions { get; set; } = CleanerOptions.TwoWeeks;
        // how do i automate this? -Naveandice
        // the roblox cache folders are shared with the real roblox client, so leave them alone unless asked
        public List<string> CleanerDirectories { get; set; } = new List<string> {
            "RobloxLogs",
            "FluxStrapLogs"
        };
        public bool EnableWindowManipulation { get; set; } = false;
        public bool FakeBorderlessFullscreen { get; set; } = false;
        public bool EnableActivityTracking { get; set; } = true;
        public bool UseDiscordRichPresence { get; set; } = true;
        public DiscordRPCStatusDisplay RichPresenceStatusDisplayType { get; set; } = DiscordRPCStatusDisplay.Name;
        public bool HideRPCButtons { get; set; } = true;
        public bool ShowAccountOnRichPresence { get; set; } = false;
        public bool ShowServerDetails { get; set; } = false;
        public ObservableCollection<CustomIntegration> CustomIntegrations { get; set; } = new();

        // voice chat (see Integrations/VoiceChat.cs), only dev builds can talk
        public bool VoiceChatEnabled { get; set; } = true;
        public int VoiceChatVolume { get; set; } = 100;
        public string VoiceChatServer { get; set; } = "";
        public string VoiceChatKey { get; set; } = "";
        public bool VoiceChatOpenMic { get; set; } = false;
        public int VoiceChatPushToTalkKey { get; set; } = 0x56; // V
        public int VoiceChatMicrophone { get; set; } = -1; // -1 = windows default

        // mod preset configuration
        public bool UseDisableAppPatch { get; set; } = false;
    }
}
