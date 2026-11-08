namespace Bloxstrap.Models.Persistable
{
    public class AppState
    {
        public string VersionGuid { get; set; } = string.Empty;

        public Dictionary<string, string> PackageHashes { get; set; } = new();

        public int Size { get; set; }

        /// <summary>
        /// Executable of the Fluxer client last launched from <see cref="VersionGuid"/>, which holds its folder name
        /// </summary>
        public string ExecutableName { get; set; } = string.Empty;
    }
}
