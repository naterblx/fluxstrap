namespace Bloxstrap.Models.Persistable
{
    public class RobloxState
    {
        public AppState Player { get; set; } = new();

        public AppState Studio { get; set; } = new();

        public List<string> ModManifest { get; set; } = new();

        /// <summary>
        /// Mod files applied to each Fluxer client folder (player2021, studio2020, ...)
        /// </summary>
        public Dictionary<string, List<string>> ClientModManifests { get; set; } = new();
    }
}
