using Microsoft.Win32;

namespace TVShowsTracker
{
    internal static class AppSettings
    {
        private const string Key = @"HKEY_CURRENT_USER\Software\TVShowsTracker";
        private const string RefreshOnStartupValue = "RefreshCatalogOnStartup";

        internal static bool RefreshCatalogOnStartup
        {
            get => !(Registry.GetValue(Key, RefreshOnStartupValue, 1) is int value) || value != 0;
            set => Registry.SetValue(Key, RefreshOnStartupValue, value ? 1 : 0, RegistryValueKind.DWord);
        }
    }
}
