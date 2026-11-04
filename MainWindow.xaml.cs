using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace TVShowsTracker
{
    public partial class MainWindow : Window
    {
        private List<Entry> entries = new List<Entry>();
        private Entry Selected => Shows.SelectedItem as Entry;
        private bool ready;
        private Mutex instance;

        public MainWindow() { InitializeComponent(); }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            instance = new Mutex(true, @"Local\SeriesDesk.Library", out bool first);
            if (!first)
            {
                instance.Dispose();
                MessageBox.Show("TVShowsTracker уже открыт.");
                Close();
                return;
            }
            Closed += (s, args) => instance.Dispose();
            try
            {
                entries = Library.Read();
                ready = true;
                BindLibrary(null);
                await Refresh();
            }
            catch (Exception ex)
            {
                OpenSearchButton.IsEnabled = ImportButton.IsEnabled = RefreshButton.IsEnabled = false;
                Status.Text = "Не удалось открыть библиотеку. " + ex.Message;
            }
        }

        private void LibraryTabChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ready && e.Source == LibraryTabs) BindLibrary(Selected);
        }

        private void BindLibrary(Entry selection)
        {
            IEnumerable<Entry> visible = entries;
            switch (LibraryTabs.SelectedIndex)
            {
                case 1:
                    visible = visible.Where(entry => entry.ShowUnwatchedIndicator);
                    break;
                case 2:
                    visible = visible.Where(entry => !entry.IsPaused &&
                        string.Equals(entry.Show.status, "Ended", StringComparison.OrdinalIgnoreCase));
                    break;
                case 3:
                    visible = visible.Where(entry => entry.IsPaused);
                    break;
            }

            var ordered = visible.OrderByDescending(entry => entry.ActivityOrder)
                .ThenBy(entry => entry.Show.name).ToList();
            Shows.ItemsSource = ordered;
            Shows.SelectedItem = ordered.Contains(selection) ? selection : ordered.FirstOrDefault();
        }

        private void OpenLibraryEntry(Entry entry)
        {
            BindLibrary(entry);
            if (Selected == entry) return;

            LibraryTabs.SelectedIndex = 0;
            Shows.SelectedItem = entry;
        }

        private async void RefreshClick(object sender, RoutedEventArgs e) => await Refresh();

        private async Task Refresh()
        {
            if (!ready || entries.Count == 0) return;
            RefreshButton.IsEnabled = ImportButton.IsEnabled = false;
            Status.Text = "Обновляем каталог…";
            int failed = 0;
            try
            {
                foreach (var entry in entries.ToArray())
                {
                    try
                    {
                        var fresh = await Catalog.Load(entry.Show.id);
                        if (!entries.Contains(entry)) continue;
                        var previous = (entry.Show, entry.Seasons, entry.Episodes);
                        entry.Show = fresh.Show;
                        entry.Seasons = fresh.Seasons;
                        entry.Episodes = fresh.Episodes;
                        if (!Save())
                        {
                            (entry.Show, entry.Seasons, entry.Episodes) = previous;
                            failed++;
                            continue;
                        }
                        var selected = Selected;
                        var season = (Seasons.SelectedItem as SeasonRow)?.Number;
                        BindLibrary(selected);
                        if (selected != null && Selected == selected) RenderDetails(season);
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        Debug.WriteLine(ex);
                    }
                    await Task.Delay(500);
                }
                Status.Text = failed == 0 ? "Каталог обновлён." : $"Не удалось обновить: {failed}. Используются сохранённые данные.";
            }
            finally { RefreshButton.IsEnabled = ImportButton.IsEnabled = true; }
        }

        private async void ImportClick(object sender, RoutedEventArgs e)
        {
            if (!ready) return;
            var dialog = new OpenFileDialog { Filter = "Текстовые файлы (*.txt)|*.txt|Все файлы (*.*)|*.*" };
            if (dialog.ShowDialog(this) != true) return;

            OpenSearchButton.IsEnabled = ImportButton.IsEnabled = RefreshButton.IsEnabled = false;
            try
            {
                var lines = File.ReadAllLines(dialog.FileName, new UTF8Encoding(false, true));
                var errors = new List<string>();
                int imported = 0;
                for (int index = 0; index < lines.Length; index++)
                {
                    var line = lines[index].Trim();
                    if (line.Length == 0) continue;

                    Status.Text = $"Импорт · строка {index + 1} из {lines.Length}…";
                    try
                    {
                        if (await ImportLine(line)) imported++;
                        else errors.Add($"Строка {index + 1} ({line}) — пропущена пользователем.");
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Строка {index + 1} ({line}) — {ex.Message}");
                    }
                }

                Status.Text = $"Импорт завершён. Обработано строк: {imported}. Пропущено: {errors.Count}.";
                if (errors.Count > 0)
                {
                    var report = new TextBox
                    {
                        Text = string.Join(Environment.NewLine, errors),
                        IsReadOnly = true,
                        TextWrapping = TextWrapping.Wrap,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        Margin = new Thickness(12)
                    };
                    new Window
                    {
                        Owner = this, Title = "Пропущенные строки импорта", Content = report,
                        Width = 560, Height = 320, WindowStartupLocation = WindowStartupLocation.CenterOwner
                    }.ShowDialog();
                }
            }
            catch (Exception ex)
            {
                Status.Text = "Не удалось импортировать файл. " + ex.Message;
            }
            finally
            {
                OpenSearchButton.IsEnabled = ImportButton.IsEnabled = RefreshButton.IsEnabled = true;
            }
        }

        private async Task<bool> ImportLine(string line)
        {
            var match = Regex.Match(line, @"^(.+?)\s+([0-9]+)(?:\.([0-9]+))?$");
            var episodeGroup = match.Groups[3];
            int episodeNumber = 0;
            if (!match.Success || !int.TryParse(match.Groups[2].Value, out int seasonNumber) || seasonNumber < 1 ||
                (episodeGroup.Success && (!int.TryParse(episodeGroup.Value, out episodeNumber) || episodeNumber < 1)))
                throw new FormatException("Ожидается название и номер сезона или сезона и серии, например «Название 2.3».");

            var title = match.Groups[1].Value.Trim();
            var results = await Catalog.Search(title);
            var candidates = results.Select(result => result.show)
                .Concat(entries.Select(item => item.Show).Where(show =>
                    string.Equals(show.name, title, StringComparison.OrdinalIgnoreCase)))
                .GroupBy(show => show.id).Select(group => group.First()).ToList();
            var exact = candidates.Where(show =>
                string.Equals(show.name, title, StringComparison.OrdinalIgnoreCase)).ToList();
            Show selected = exact.Count == 1 ? exact[0] : null;
            if (selected == null)
            {
                var dialog = new AddSeriesWindow(show =>
                {
                    selected = show;
                    return Task.FromResult<string>(null);
                }, title, line, candidates) { Owner = this };
                if (dialog.ShowDialog() != true) return false;
            }

            var entry = entries.FirstOrDefault(item => item.Show.id == selected.id) ?? await Catalog.Load(selected.id);
            if (!entry.Episodes.Any(episode => episode.season == seasonNumber &&
                (!episodeGroup.Success || episode.number == episodeNumber)))
                throw new InvalidOperationException("Указанный сезон или серия отсутствует в каталоге.");

            var watched = entry.Episodes.Where(episode => episode.season < seasonNumber ||
                (episode.season == seasonNumber && (!episodeGroup.Success || episode.number <= episodeNumber)));
            bool added = !entries.Contains(entry);
            if (added) entries.Add(entry);
            if (!UpdateProgress(item =>
            {
                item.Watched.UnionWith(watched.Select(episode => episode.id));
                if (item.Current.HasValue && item.Watched.Contains(item.Current.Value)) item.Current = null;
            }, entry))
            {
                if (added) entries.Remove(entry);
                throw new InvalidOperationException(Status.Text);
            }
            return true;
        }

        private void OpenSearch(object sender, RoutedEventArgs e)
        {
            new AddSeriesWindow(AddSeries) { Owner = this }.ShowDialog();
        }

        private async Task<string> AddSeries(Show show)
        {
            var existing = entries.FirstOrDefault(item => item.Show.id == show.id);
            if (existing != null)
            {
                OpenLibraryEntry(existing);
                Status.Text = "Сериал уже в библиотеке.";
                return null;
            }

            var entry = await Catalog.Load(show.id);
            entries.Add(entry);
            if (!Save())
            {
                entries.Remove(entry);
                return Status.Text;
            }
            OpenLibraryEntry(entry);
            Status.Text = "Сериал добавлен.";
            return null;
        }

        private void ShowMenuOpening(object sender, ContextMenuEventArgs e)
        {
            var row = (ListBoxItem)sender;
            if (!(row.DataContext is Entry entry)) return;

            var menu = row.ContextMenu;
            menu.Items.Clear();
            var mark = new MenuItem { Header = "Отметить сезон как просмотренный" };
            foreach (var season in entry.Episodes.GroupBy(episode => episode.season).OrderBy(group => group.Key))
            {
                var item = new MenuItem
                {
                    Header = $"Сезон {season.Key}",
                    IsEnabled = season.Any(episode => !entry.Watched.Contains(episode.id))
                };
                item.Click += (source, args) => MarkSeasonWatched(entry, season.Key);
                mark.Items.Add(item);
            }
            mark.IsEnabled = mark.Items.Count > 0;
            var pause = new MenuItem
            {
                Header = entry.IsPaused ? "Снять с паузы" : "Поставить на паузу",
                IsEnabled = CanTogglePause(entry)
            };
            pause.Click += (source, args) => TogglePause(entry);
            var remove = new MenuItem { Header = "Удалить сериал из списка…" };
            remove.Click += (source, args) => RemoveEntry(entry);
            menu.Items.Add(mark);
            menu.Items.Add(pause);
            menu.Items.Add(new Separator());
            menu.Items.Add(remove);
        }

        private void RemoveEntry(Entry entry)
        {
            if (entry == null || MessageBox.Show(this, $"Удалить «{entry.Show.name}» вместе с отметками просмотра?",
                "Удалить сериал", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            entries.Remove(entry);
            if (!Save())
            {
                entries.Add(entry);
                return;
            }
            BindLibrary(Selected == entry ? entries.FirstOrDefault() : Selected);
            Status.Text = "Сериал удалён.";
        }

        private bool Save()
        {
            try { Library.Save(entries); return true; }
            catch (Exception ex)
            {
                Status.Text = "Не удалось сохранить библиотеку. " + ex.Message;
                return false;
            }
        }

        private void ShowChanged(object sender, SelectionChangedEventArgs e) => RenderDetails();

        private void RenderDetails(int? preferredSeason = null)
        {
            var entry = Selected;
            Details.Visibility = entry == null ? Visibility.Collapsed : Visibility.Visible;
            Empty.Visibility = entry == null ? Visibility.Visible : Visibility.Collapsed;
            if (entry == null) return;

            ShowTitle.Text = entry.Show.Label;
            PauseButton.Content = entry.IsPaused ? "Снять с паузы" : "Поставить на паузу";
            PauseButton.IsEnabled = CanTogglePause(entry);
            Cover.Source = null;
            if (Uri.TryCreate(entry.Show.image?.medium, UriKind.Absolute, out var poster) && poster.Scheme == "https")
            {
                try { Cover.Source = new BitmapImage(poster); }
                catch (Exception ex) { Debug.WriteLine(ex); }
            }

            var episodes = Ordered(entry).ToList();
            int watched = episodes.Count(episode => entry.Watched.Contains(episode.id));
            var current = episodes.FirstOrDefault(episode => episode.id == entry.Current)
                ?? episodes.FirstOrDefault(episode => !entry.Watched.Contains(episode.id));
            var seasons = entry.Seasons.Select(season => season.number).Concat(episodes.Select(episode => episode.season))
                .Distinct().OrderBy(number => number).Select(number =>
                {
                    int available = episodes.Count(episode => episode.season == number);
                    int planned = entry.Seasons.FirstOrDefault(season => season.number == number)?.episodeOrder ?? available;
                    return new SeasonRow
                    {
                        Number = number,
                        Label = $"Сезон {number} · {available}" + (planned > available ? $" из {planned}" : "")
                    };
                }).ToList();

            ProgressText.Text = $"Просмотрено {watched} из {episodes.Count} · сезонов {seasons.Count}";
            Progress.Value = episodes.Count == 0 ? 0 : 100.0 * watched / episodes.Count;
            CurrentText.Text = current == null
                ? (episodes.Count == 0 ? "Список серий пока пуст" : "Все серии просмотрены")
                : $"{(entry.Current == current.id ? "Смотрю" : "Далее")}: S{current.season:00}E{current.number:00} · {current.name}";
            Seasons.ItemsSource = seasons;
            Seasons.SelectedItem = seasons.FirstOrDefault(season => season.Number == (preferredSeason ?? current?.season))
                ?? seasons.FirstOrDefault();
            RenderEpisodes();
        }

        private static IEnumerable<Episode> Ordered(Entry entry) =>
            entry.Episodes.OrderBy(episode => episode.season).ThenBy(episode => episode.number);

        private void SeasonChanged(object sender, SelectionChangedEventArgs e) => RenderEpisodes();

        private void RenderEpisodes()
        {
            var entry = Selected;
            var season = Seasons.SelectedItem as SeasonRow;
            var rows = entry == null || season == null ? new List<EpisodeRow>() : Ordered(entry)
                .Where(episode => episode.season == season.Number)
                .Select(episode => new EpisodeRow
                {
                    Id = episode.id,
                    Label = $"{episode.number:00} · {episode.name}" +
                        ReleaseLabel(episode.airdate) +
                        (entry.Current == episode.id ? "  ◂ смотрю" : ""),
                    Watched = entry.Watched.Contains(episode.id)
                }).ToList();
            Episodes.ItemsSource = rows;
            MarkSeasonButton.IsEnabled = rows.Any(row => !row.Watched);
        }

        private static string ReleaseLabel(string airdate)
        {
            return DateTime.TryParseExact(airdate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var release)
                ? " · Выход " + release.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)
                : string.Empty;
        }

        private void ToggleWatched(object sender, RoutedEventArgs e)
        {
            var box = (CheckBox)sender;
            if (!(box.DataContext is EpisodeRow row)) return;
            UpdateProgress(entry =>
            {
                if (box.IsChecked == true)
                {
                    entry.Watched.Add(row.Id);
                    if (entry.Current == row.Id) entry.Current = null;
                }
                else entry.Watched.Remove(row.Id);
            });
        }

        private void SetCurrent(object sender, RoutedEventArgs e)
        {
            if (((Button)sender).DataContext is EpisodeRow row)
                UpdateProgress(entry => entry.Current = row.Id);
        }

        private void TogglePause(object sender, RoutedEventArgs e) => TogglePause(Selected);

        private static bool CanTogglePause(Entry entry) =>
            entry.IsPaused || entry.Episodes.Count == 0 || entry.HasUnwatchedEpisodes ||
            !string.Equals(entry.Show.status, "Ended", StringComparison.OrdinalIgnoreCase);

        private void TogglePause(Entry entry)
        {
            if (entry == null || !CanTogglePause(entry)) return;

            var selection = Selected;
            var season = (Seasons.SelectedItem as SeasonRow)?.Number;
            bool wasPaused = entry.IsPaused;
            entry.IsPaused = !wasPaused;
            if (!Save())
            {
                entry.IsPaused = wasPaused;
                return;
            }

            BindLibrary(selection);
            if (Selected == selection) RenderDetails(season);
            Status.Text = entry.IsPaused ? "Сериал поставлен на паузу." : "Сериал снят с паузы.";
        }

        private void MarkSeason(object sender, RoutedEventArgs e)
        {
            if (!(Seasons.SelectedItem is SeasonRow season)) return;
            MarkSeasonWatched(Selected, season.Number);
        }

        private void MarkSeasonWatched(Entry target, int seasonNumber)
        {
            UpdateProgress(entry =>
            {
                foreach (var episode in entry.Episodes.Where(episode => episode.season == seasonNumber))
                    entry.Watched.Add(episode.id);
                if (entry.Current.HasValue && entry.Watched.Contains(entry.Current.Value)) entry.Current = null;
            }, target);
        }

        private bool UpdateProgress(Action<Entry> update, Entry target = null)
        {
            var entry = target ?? Selected;
            if (entry == null) return false;
            var previous = new HashSet<int>(entry.Watched);
            var current = entry.Current;
            var season = (Seasons.SelectedItem as SeasonRow)?.Number;
            var activity = entry.ActivityOrder;
            var selection = Selected;
            update(entry);
            bool changed = current != entry.Current || !previous.SetEquals(entry.Watched);
            if (changed)
                entry.ActivityOrder = entries.Max(item => item.ActivityOrder) + 1;

            bool saved = Save();
            if (!saved)
            {
                entry.Watched = previous;
                entry.Current = current;
                entry.ActivityOrder = activity;
            }
            else
            {
                if (changed)
                {
                    BindLibrary(selection);
                    if (Shows.Items.Contains(entry)) Shows.ScrollIntoView(entry);
                }
                Status.Text = "Прогресс сохранён.";
            }
            if (selection != null && Selected == selection && (entry == selection || changed)) RenderDetails(season);
            return saved;
        }

        private void ThumbnailFailed(object sender, ExceptionRoutedEventArgs e) =>
            ((Image)sender).SetCurrentValue(Image.SourceProperty, null);

        private void PosterFailed(object sender, ExceptionRoutedEventArgs e) => Cover.Source = null;
        private void OpenSource(object sender, RoutedEventArgs e) => OpenLink(Selected?.Show.url);
        private void OpenAttribution(object sender, RoutedEventArgs e) => OpenLink("https://www.tvmaze.com/api#licensing");

        private void OpenLink(string link)
        {
            if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Host != "www.tvmaze.com" ||
                (uri.Scheme != "https" && uri.Scheme != "http")) return;
            try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception ex) { Status.Text = "Не удалось открыть ссылку. " + ex.Message; }
        }

        public sealed class SeasonRow
        {
            public int Number { get; set; }
            public string Label { get; set; }
        }

        public sealed class EpisodeRow
        {
            public int Id { get; set; }
            public string Label { get; set; }
            public bool Watched { get; set; }
            public string CurrentLabel => "Смотрю: " + Label;
        }
    }
}

