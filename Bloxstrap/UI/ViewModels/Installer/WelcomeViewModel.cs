namespace Bloxstrap.UI.ViewModels.Installer
{
    public class WelcomeViewModel : NotifyPropertyChangedViewModel
    {
        // the translated welcome text points at fishstrap's download sites, so fluxstrap uses its own
        public string MainText =>
            "Thank you for downloading FluxStrap, a fork of Fishstrap made for the Fluxer revival.\n\n" +
            "FluxStrap is based on [Fishstrap](https://github.com/fishstrap/fishstrap), which is itself based on Bloxstrap.\n\n" +
            "This installation process will be quick and simple, and you will be able to configure any of FluxStrap's settings after installation.";

        public bool CanContinue { get; set; } = false;
    }
}
