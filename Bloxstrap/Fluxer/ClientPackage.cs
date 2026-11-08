namespace Bloxstrap.Fluxer
{
    /// <summary>
    /// One line of the Fluxer client manifest: version|zip|md5|exe|folder
    /// </summary>
    public class ClientPackage
    {
        public string Version { get; init; } = "";

        public string ZipName { get; init; } = "";

        public string Md5 { get; init; } = "";

        public string ExecutableName { get; init; } = "";

        public string Folder { get; init; } = "";

        public string Year => Version.Length >= 4 ? Version[..4] : Version;

        public bool IsStudio => Version.EndsWith("S", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The 2026 client is a private download that needs a login cookie
        /// </summary>
        public bool IsPrivate => Year == "2026" && !IsStudio;

        public string Label
        {
            get
            {
                if (IsStudio)
                    return $"Fluxer Studio {Year}";

                if (IsPrivate)
                    return "Rokori client";

                return $"Fluxer {Year}";
            }
        }

        public string DownloadPath => Path.Combine(Paths.Downloads, Md5);

        public string InstallDirectory => Path.Combine(Paths.Versions, Folder);

        public string ExecutablePath => Path.Combine(InstallDirectory, ExecutableName);

        public string Md5Path => Path.Combine(InstallDirectory, ".md5");

        public bool IsInstalled => File.Exists(ExecutablePath)
            && File.Exists(Md5Path)
            && File.ReadAllText(Md5Path).Trim().Equals(Md5, StringComparison.OrdinalIgnoreCase);

        public static ClientPackage? Parse(string line)
        {
            string[] parts = line.Trim().Split('|');

            if (parts.Length < 5)
                return null;

            return new ClientPackage
            {
                Version = parts[0],
                ZipName = parts[1],
                Md5 = parts[2].ToLowerInvariant(),
                ExecutableName = parts[3],
                Folder = parts[4]
            };
        }

        public override string ToString() => $"{Version} ({Folder}, {ExecutableName}, {Md5})";
    }
}
