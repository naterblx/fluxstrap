namespace Bloxstrap.AppData
{
    public class RobloxStudioData : CommonAppData, IAppData
    {
        public string ProductName => "Fluxer Studio";

        public override string BinaryType => "WindowsStudio64";

        public string RegistryName => "RobloxStudio";

        public override string ExecutableName => String.IsNullOrEmpty(State.ExecutableName) ? App.RobloxStudioAppName : State.ExecutableName;

        public override AppState State => App.RobloxState.Prop.Studio;
    }
}
