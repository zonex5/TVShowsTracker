using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;

namespace TVShowsTracker
{
    public partial class AddSeriesWindow : Window
    {
        private readonly Func<Show, Task<string>> addSeries;
        private readonly Func<Entry, string> saveManual;
        private bool adding;

        internal AddSeriesWindow(Func<Show, Task<string>> addSeries, Func<Entry, string> saveManual = null)
        {
            this.addSeries = addSeries;
            this.saveManual = saveManual;
            InitializeComponent();
            if (saveManual != null) ManualButton.Visibility = Visibility.Visible;
            Loaded += (sender, args) => Query.Focus();
            Closing += (sender, args) => args.Cancel = adding;
        }

        internal AddSeriesWindow(Func<Show, Task<string>> selectSeries, string query, string line, List<Show> candidates)
            : this(selectSeries)
        {
            Title = "Выбрать сериал для импорта";
            ImportLine.Text = line;
            ImportLinePanel.Visibility = SkipButton.Visibility = Visibility.Visible;
            Query.Text = query;
            Loaded += (sender, args) => Query.SelectAll();
            ShowResults(candidates);
        }

        private void SkipClick(object sender, RoutedEventArgs e) => Close();

        private void ManualClick(object sender, RoutedEventArgs e)
        {
            var dialog = new ManualSeriesWindow(saveManual, Query.Text.Trim()) { Owner = this };
            if (dialog.ShowDialog() == true) DialogResult = true;
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape) return;

            e.Handled = true;
            if (!adding) Close();
        }

        private async void SearchClick(object sender, RoutedEventArgs e) => await Search();

        private async Task Search()
        {
            string query = Query.Text.Trim();
            if (query.Length == 0)
            {
                Status.Text = "Введите название сериала.";
                return;
            }
            SearchButton.IsEnabled = false;
            Results.Visibility = Visibility.Collapsed;
            Status.Text = "Ищем сериалы…";
            try
            {
                var results = (await Catalog.Search(query)).Select(result => result.show).ToList();
                ShowResults(results);
            }
            catch (Exception ex) { Status.Text = "Ошибка поиска. " + ex.Message; }
            finally { SearchButton.IsEnabled = true; }
        }

        private void ShowResults(List<Show> results)
        {
            Results.ItemsSource = results;
            Results.SelectedIndex = 0;
            Results.Visibility = results.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            Status.Text = results.Count == 0 ? "Ничего не найдено. Измените запрос."
                : "Дважды щёлкните по нужному сериалу или выберите его клавишей Enter.";
        }

        private void ThumbnailFailed(object sender, ExceptionRoutedEventArgs e) =>
            ((Image)sender).SetCurrentValue(Image.SourceProperty, null);

        private async void ResultDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;

            e.Handled = true;
            await Add(((ListBoxItem)sender).Content as Show);
        }

        private async void ResultsKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;

            e.Handled = true;
            await Add(Results.SelectedItem as Show);
        }

        private async Task Add(Show show)
        {
            if (adding || show == null) return;

            adding = true;
            Results.IsEnabled = SearchButton.IsEnabled = ManualButton.IsEnabled = false;
            Status.Text = "Загружаем сезоны и серии…";
            try
            {
                var error = await addSeries(show);
                if (error != null)
                {
                    Status.Text = error;
                    return;
                }
                adding = false;
                DialogResult = true;
            }
            catch (Exception ex) { Status.Text = "Не удалось добавить сериал. " + ex.Message; }
            finally
            {
                adding = false;
                Results.IsEnabled = SearchButton.IsEnabled = ManualButton.IsEnabled = true;
            }
        }
    }
}
