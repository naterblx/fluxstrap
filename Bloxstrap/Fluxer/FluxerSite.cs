namespace Bloxstrap.Fluxer
{
    /// <summary>
    /// Everything that points at the Fluxer revival lives here, so the site can be moved in one place
    /// </summary>
    public static class FluxerSite
    {
        public const string Domain = "fluxr.lol";

        public const string Url = $"https://{Domain}";

        public const string GamesUrl = $"{Url}/games";

        public const string DownloadsUrl = $"{Url}/downloads";

        public const string ManifestUrl = $"{Url}/setup/manifest.txt";

        public const string SetupUrl = $"{Url}/setup/";

        // the 2026 (Rokori) client is only handed out to logged in users
        public const string PrivateSetupUrl = $"{Url}/setup-private/";

        public const string ApiUrl = $"{Url}/apisite";

        public const string AuthenticationUrl = $"{Url}/Login/Negotiate.ashx";

        public const string PlaceLauncherUrl = $"{Url}/Game/PlaceLauncher.ashx";

        public const string SecurityCookie = ".FLUXERSECURITY";

        public const string DiscordApplicationId = "1563358011416780930";

        public const string DiscordImageKey = "fluxer";

        /// <summary>
        /// The domain baked into the clients that gets patched to <see cref="Domain"/> at runtime
        /// </summary>
        public const string PatchedDomain = "roblox.com";

        public static readonly string[] Schemes = { "fluxer-player", "fluxerclient" };

        /// <summary>
        /// Where the official Fluxer launcher keeps its clients
        /// </summary>
        public static string LauncherDirectory => Path.Combine(Paths.LocalAppData, "Fluxer");

        public static string LauncherExecutable => Path.Combine(LauncherDirectory, "FluxerLauncher.exe");

        public static string GetGamePageUrl(long placeId) => $"{GamesUrl}/{placeId}/--";

        /// <summary>
        /// Rewrites old site hosts to the current one, as the official launcher does
        /// </summary>
        public static string ToPublicHost(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return url;

            switch (uri.Host.ToLowerInvariant())
            {
                case "projex.zip":
                case "www.projex.zip":
                case "www." + Domain:
                    return new UriBuilder(uri) { Scheme = "https", Host = Domain, Port = -1 }.Uri.AbsoluteUri;

                default:
                    return url;
            }
        }
    }
}
