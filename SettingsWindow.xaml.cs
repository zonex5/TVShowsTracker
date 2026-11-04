using System;
using System.IO;
using System.Security;
using System.Windows;

namespace TVShowsTracker
{
    public partial class SettingsWindow : Window
    {
        internal SettingsWindow(Window owner)
        {
            InitializeComponent();
            Owner = owner;
            try
            {
                RefreshOnStartup.IsChecked = AppSettings.RefreshCatalogOnStartup;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException)
            {
                RefreshOnStartup.IsEnabled = false;
                SettingsError.Text = "Не удалось прочитать настройки. " + ex.Message;
                SettingsError.Visibility = Visibility.Visible;
            }
        }

        private void RefreshOnStartupClick(object sender, RoutedEventArgs e)
        {
            bool enabled = RefreshOnStartup.IsChecked == true;
            try
            {
                AppSettings.RefreshCatalogOnStartup = enabled;
                SettingsError.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException)
            {
                RefreshOnStartup.IsChecked = !enabled;
                SettingsError.Text = "Не удалось сохранить настройку. " + ex.Message;
                SettingsError.Visibility = Visibility.Visible;
            }
        }

        private void CloseClick(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
