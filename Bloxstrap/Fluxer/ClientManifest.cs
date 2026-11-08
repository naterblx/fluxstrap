namespace Bloxstrap.Fluxer
{
    public class ClientManifest
    {
        public List<ClientPackage> Packages { get; } = new();

        public IEnumerable<ClientPackage> Players => Packages.Where(x => !x.IsStudio && !x.IsPrivate);

        public IEnumerable<ClientPackage> Studios => Packages.Where(x => x.IsStudio);

        public ClientManifest(string data)
        {
            foreach (string line in data.Split('\n'))
            {
                var package = ClientPackage.Parse(line);

                if (package is not null)
                    Packages.Add(package);
            }
        }

        public ClientPackage? Find(string version) => Packages.FirstOrDefault(x => x.Version.Equals(version, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Distinct installable folders, newest year first
        /// </summary>
        public IEnumerable<ClientPackage> GetDistinctFolders(bool studio)
        {
            return (studio ? Studios : Players)
                .GroupBy(x => x.Folder)
                .Select(x => x.First())
                .OrderByDescending(x => x.Year);
        }

        public static async Task<ClientManifest> Fetch(CancellationToken token = default)
        {
            string data = await App.HttpClient.GetStringAsync($"{FluxerSite.ManifestUrl}?t={DateTime.UtcNow.Ticks}", token);

            var manifest = new ClientManifest(data);

            if (!manifest.Packages.Any())
                throw new InvalidHTTPResponseException("The Fluxer client manifest is empty");

            return manifest;
        }

        /// <summary>
        /// Builds a package for a client that's already on disk, for when the site can't be reached
        /// </summary>
        public static ClientPackage? FromInstalled(string version)
        {
            string year = version.Length >= 4 ? version[..4] : version;
            bool studio = version.EndsWith("S", StringComparison.OrdinalIgnoreCase);

            string folder = (studio ? "studio" : "player") + year;
            string directory = Path.Combine(Paths.Versions, folder);
            string md5Path = Path.Combine(directory, ".md5");

            if (!File.Exists(md5Path))
                return null;

            string[] executables = studio
                ? new[] { App.RobloxStudioAppName }
                : new[] { App.FluxerPlayerAppName, App.RobloxPlayerAppName };

            string? executable = executables.FirstOrDefault(x => File.Exists(Path.Combine(directory, x)));

            if (executable is null)
                return null;

            return new ClientPackage
            {
                Version = version,
                Folder = folder,
                ExecutableName = executable,
                Md5 = File.ReadAllText(md5Path).Trim().ToLowerInvariant()
            };
        }
    }
}
