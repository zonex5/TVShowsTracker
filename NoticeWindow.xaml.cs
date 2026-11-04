using System.Windows;
using System.Windows.Media;

namespace TVShowsTracker
{
    public partial class NoticeWindow : Window
    {
        internal NoticeWindow(Window owner, string title, string message, string report = null, bool confirm = false)
        {
            InitializeComponent();
            Owner = owner;
            Title = title;
            Message.Text = message;
            if (report != null)
            {
                Report.Text = report;
                Report.Visibility = Visibility.Visible;
            }
            if (confirm)
            {
                Badge.Background = (Brush)FindResource("SoftRed");
                BadgeText.Foreground = (Brush)FindResource("Danger");
                BadgeText.Text = "!";
                ConfirmButton.Visibility = Visibility.Visible;
                CloseButton.Content = "Нет";
            }
            Loaded += (sender, args) => CloseButton.Focus();
        }

        private void ConfirmClick(object sender, RoutedEventArgs e) => DialogResult = true;
        private void CloseClick(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
