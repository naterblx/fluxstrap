namespace Bloxstrap.AppData
{
    public class RobloxPlayerData : CommonAppData, IAppData
    {
        public string ProductName => "Fluxer";

        public override string BinaryType => "WindowsPlayer";

        public string RegistryName => "RobloxPlayer";

        public override string ExecutableName => String.IsNullOrEmpty(State.ExecutableName) ? App.FluxerPlayerAppName : State.ExecutableName;

        public override AppState State => App.RobloxState.Prop.Player;
    }
}
