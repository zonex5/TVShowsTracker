using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
        private CancellationTokenSource refreshCancellation;
        private Task refreshTask = Task.CompletedTask;

        public MainWindow() { InitializeComponent(); }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            instance = new Mutex(true, @"Local\TVShowsTracker.Library", out bool first);
            if (!first)
            {
                instance.Dispose();
                new NoticeWindow(this, "TVShowsTracker уже открыт", "Переключитесь на открытое окно дневника.").ShowDialog();
                Close();
                return;
            }
            Closed += (s, args) =>
            {
                refreshCancellation?.Cancel();
                instance.Dispose();
            };
            try
            {
                entries = Library.Read();
                BindLibrary(null);
                ready = true;
                UpdateRefreshSeriesButton();
            }
            catch (Exception ex)
            {
                OpenSearchButton.IsEnabled = ImportButton.IsEnabled = RefreshButton.IsEnabled = false;
                LibrarySearchButton.IsEnabled = false;
                ShowError("Не удалось открыть библиотеку. " + ex.Message);
                return;
            }
            bool refreshOnStartup;
            try
            {
                refreshOnStartup = AppSettings.RefreshCatalogOnStartup;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException)
            {
                ShowError("Не удалось прочитать настройки. " + ex.Message);
                return;
            }
            if (refreshOnStartup) refreshTask = Refresh();
        }

        private void OpenSettings(object sender, RoutedEventArgs e) => new SettingsWindow(this).ShowDialog();

        private void LibraryTabChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ready && e.Source == LibraryTabs) BindLibrary(Selected);
        }

        private void ToggleLibrarySearch(object sender, RoutedEventArgs e)
        {
            if (LibrarySearchPanel.Visibility == Visibility.Visible)
            {
                HideLibrarySearch();
                return;
            }

            LibrarySearchPanel.Visibility = Visibility.Visible;
            LibraryQuery.Focus();
        }

        private void WindowPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape || LibrarySearchPanel.Visibility != Visibility.Visible) return;

            HideLibrarySearch();
            e.Handled = true;
        }

        private void HideLibrarySearch()
        {
            LibrarySearchPanel.Visibility = Visibility.Collapsed;
            LibraryQuery.Clear();
            LibrarySearchButton.Focus();
        }

        private void LibraryQueryChanged(object sender, TextChangedEventArgs e)
        {
            if (!ready) return;

            var selection = Selected;
            var season = (Seasons.SelectedItem as SeasonRow)?.Number;
            BindLibrary(selection);
            if (selection != null && Selected == selection) RenderDetails(season);
        }

        private void ClearLibraryQuery(object sender, RoutedEventArgs e)
        {
            LibraryQuery.Focus();
            LibraryQuery.Clear();
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

            string query = LibraryQuery.Text.Trim();
            if (query.Length > 0)
                visible = visible.Where(entry =>
                    entry.Show.name?.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    entry.Show.RussianTitle?.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);

            LibraryEmptyMessage.Text = query.Length == 0 ? "В этом разделе пока нет сериалов" : "Сериалы не найдены";
            var ordered = visible.OrderByDescending(entry => entry.ActivityOrder)
                .ThenBy(entry => entry.Show.name).ToList();
            Shows.ItemsSource = ordered;
            Shows.SelectedItem = ordered.Contains(selection) ? selection : ordered.FirstOrDefault();
        }

        private void OpenLibraryEntry(Entry entry)
        {
            BindLibrary(entry);
            if (Selected == entry) return;

            LibraryQuery.Clear();
            if (!Shows.Items.Contains(entry)) LibraryTabs.SelectedIndex = 0;
            Shows.SelectedItem = entry;
        }

        private void RefreshClick(object sender, RoutedEventArgs e)
        {
            if (refreshCancellation != null) refreshCancellation.Cancel();
            else refreshTask = Refresh();
        }

        private void RefreshSeriesClick(object sender, RoutedEventArgs e)
        {
            var entry = Selected;
            if (entry == null || entry.Show.IsManual || !ImportButton.IsEnabled || refreshCancellation != null) return;
            refreshTask = Refresh(entry);
        }

        private async Task Refresh(Entry singleEntry = null)
        {
            if (!ready || refreshCancellation != null) return;
            var snapshot = singleEntry == null
                ? entries.Where(entry => !entry.Show.IsManual && !entry.IsPaused).ToArray()
                : new[] { singleEntry };
            if (snapshot.Length == 0)
            {
                SetRefreshButton(false, "В библиотеке нет сериалов из TVmaze без паузы.");
                return;
            }
            var cancellation = new CancellationTokenSource();
            refreshCancellation = cancellation;
            var token = cancellation.Token;
            SetRefreshButton(true);
            int failed = 0;
            string result = null;
            try
            {
                for (int index = 0; index < snapshot.Length; index++)
                {
                    if (index > 0) await Task.Delay(500, token);
                    var entry = snapshot[index];
                    if (!entries.Contains(entry) || (singleEntry == null && entry.IsPaused)) continue;

                    RefreshStatus.Text = singleEntry == null
                        ? $"Обновляем каталог · {index + 1} из {snapshot.Length}…"
                        : $"Обновляем сериал · {entry.Show.Label}…";
                    try
                    {
                        var fresh = await Catalog.Load(entry.Show.id, token, entry.Show.RussianTitle);
                        token.ThrowIfCancellationRequested();
                        if (!entries.Contains(entry) || (singleEntry == null && entry.IsPaused)) continue;
                        var previous = (entry.Show, entry.Seasons, entry.Episodes);
                        entry.Show = fresh.Show;
                        entry.Seasons = fresh.Seasons;
                        entry.Episodes = fresh.Episodes;
                        if (Save() != null)
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
                    catch (Exception ex) when (!(ex is CatalogUnavailableException) && !token.IsCancellationRequested)
                    {
                        failed++;
                        Debug.WriteLine(ex);
                    }
                }
                result = failed == 0
                    ? (singleEntry == null ? "Каталог обновлён." : $"Сериал обновлён: {singleEntry.Show.Label}.")
                    : $"Не удалось обновить: {failed}. Используются сохранённые данные.";
            }
            catch (Exception) when (token.IsCancellationRequested)
            {
                result = "Обновление каталога остановлено.";
            }
            catch (CatalogUnavailableException ex)
            {
                Debug.WriteLine(ex);
                failed++;
                result = ex.Message + " Библиотека доступна.";
            }
            finally
            {
                refreshCancellation = null;
                cancellation.Dispose();
                SetRefreshButton(false, result);
                if (singleEntry != null && failed > 0) ShowError(result);
            }
        }

        private void SetRefreshButton(bool refreshing, string result = null)
        {
            string label = refreshing ? "Остановить обновление каталога" : "Обновить каталог";
            RefreshStatusBar.Visibility = refreshing ? Visibility.Visible : Visibility.Collapsed;
            RefreshButton.ToolTip = result == null ? label : label + Environment.NewLine + result;
            AutomationProperties.SetName(RefreshButton, label);
            AutomationProperties.SetHelpText(RefreshButton, result ?? string.Empty);
            RefreshGlyph.Data = (Geometry)FindResource(refreshing ? "StopIcon" : "RefreshIcon");
            UpdateRefreshSeriesButton();
        }

        private void UpdateRefreshSeriesButton()
        {
            var entry = Selected;
            RefreshSeriesButton.Visibility = entry != null && !entry.Show.IsManual
                ? Visibility.Visible : Visibility.Collapsed;
            RefreshSeriesButton.IsEnabled = ready && refreshCancellation == null && ImportButton.IsEnabled;
            RefreshSeriesButton.ToolTip = refreshCancellation == null
                ? "Обновить сериал" : "Дождитесь завершения обновления";
        }

        private async void ImportClick(object sender, RoutedEventArgs e)
        {
            if (!ready) return;
            var dialog = new OpenFileDialog { Filter = "Текстовые файлы (*.txt)|*.txt|Все файлы (*.*)|*.*" };
            if (dialog.ShowDialog(this) != true) return;

            OpenSearchButton.IsEnabled = ImportButton.IsEnabled = RefreshButton.IsEnabled = false;
            UpdateRefreshSeriesButton();
            try
            {
                refreshCancellation?.Cancel();
                await refreshTask;
                var lines = File.ReadAllLines(dialog.FileName, new UTF8Encoding(false, true));
                var errors = new List<string>();
                int imported = 0;
                for (int index = 0; index < lines.Length; index++)
                {
                    var line = lines[index].Trim();
                    if (line.Length == 0) continue;

                    ImportButton.ToolTip = $"Импорт · строка {index + 1} из {lines.Length}…";
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

                string summary = $"Обработано строк: {imported}. Пропущено: {errors.Count}.";
                string report = errors.Count > 0 ? string.Join(Environment.NewLine, errors) : null;
                new NoticeWindow(this, "Импорт завершён", summary, report).ShowDialog();
            }
            catch (Exception ex)
            {
                ShowError("Не удалось импортировать файл. " + ex.Message);
            }
            finally
            {
                ImportButton.ToolTip = AutomationProperties.GetName(ImportButton);
                OpenSearchButton.IsEnabled = ImportButton.IsEnabled = RefreshButton.IsEnabled = true;
                UpdateRefreshSeriesButton();
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

            var watched = entry.Episodes.Where(episode => entry.IsEpisodeReleased(episode) &&
                (episode.season < seasonNumber ||
                (episode.season == seasonNumber && (!episodeGroup.Success || episode.number <= episodeNumber))));
            bool added = !entries.Contains(entry);
            if (added) entries.Add(entry);
            string error = UpdateProgress(item =>
            {
                item.Watched.UnionWith(watched.Select(episode => episode.id));
                if (item.Current.HasValue && item.Watched.Contains(item.Current.Value)) item.Current = null;
            }, entry);
            if (error != null)
            {
                if (added) entries.Remove(entry);
                throw new InvalidOperationException(error);
            }
            return true;
        }

        private void OpenSearch(object sender, RoutedEventArgs e)
        {
            new AddSeriesWindow(AddSeries, AddManualSeries) { Owner = this }.ShowDialog();
        }

        private string AddManualSeries(Entry entry)
        {
            entry.Show.id = entries.Where(item => item.Show.IsManual)
                .Select(item => item.Show.id).DefaultIfEmpty(0).Min() - 1;
            return SaveNewEntry(entry);
        }

        private async Task<string> AddSeries(Show show)
        {
            var existing = entries.FirstOrDefault(item => item.Show.id == show.id);
            if (existing != null)
            {
                OpenLibraryEntry(existing);
                return null;
            }

            var entry = await Catalog.Load(show.id);
            return SaveNewEntry(entry);
        }

        private string SaveNewEntry(Entry entry)
        {
            entries.Add(entry);
            string error = Save();
            if (error != null)
            {
                entries.Remove(entry);
                return error;
            }
            OpenLibraryEntry(entry);
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
                    IsEnabled = season.Any(episode => entry.IsEpisodeReleased(episode) && !entry.Watched.Contains(episode.id))
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
            var copyTitle = new MenuItem { Header = "Скопировать название сериала" };
            copyTitle.Click += (source, args) => CopyShowTitle(entry);
            var remove = new MenuItem
            {
                Header = "Удалить сериал из списка…",
                Foreground = (Brush)FindResource("Danger")
            };
            remove.Click += (source, args) => RemoveEntry(entry);
            menu.Items.Add(mark);
            menu.Items.Add(pause);
            menu.Items.Add(copyTitle);
            if (entry.Show.IsManual)
            {
                var edit = new MenuItem { Header = "Редактировать сериал…" };
                edit.Click += (source, args) => EditManualSeries(entry);
                menu.Items.Add(edit);
            }
            menu.Items.Add(new Separator());
            menu.Items.Add(remove);
        }

        private void EditManualSeries(Entry entry)
        {
            new ManualSeriesWindow(updated =>
            {
                var previous = (entry.Show, entry.Seasons, entry.Episodes);
                updated.Show.id = entry.Show.id;
                (entry.Show, entry.Seasons, entry.Episodes) = (updated.Show, updated.Seasons, updated.Episodes);
                string error = Save();
                if (error != null)
                {
                    (entry.Show, entry.Seasons, entry.Episodes) = previous;
                    return error;
                }

                var selection = Selected;
                var season = (Seasons.SelectedItem as SeasonRow)?.Number;
                BindLibrary(selection);
                if (Selected == selection) RenderDetails(season);
                return null;
            }, entry.Show.name, entry) { Owner = this }.ShowDialog();
        }

        private void CopySelectedShowTitle(object sender, RoutedEventArgs e)
        {
            var entry = Selected;
            if (entry != null) CopyShowTitle(entry);
        }

        private void CopyShowTitle(Entry entry)
        {
            try { Clipboard.SetText(entry.Show.name ?? string.Empty); }
            catch (System.Runtime.InteropServices.ExternalException ex)
            {
                ShowError("Не удалось скопировать название сериала. " + ex.Message);
            }
        }

        private void RemoveEntry(Entry entry)
        {
            if (entry == null || new NoticeWindow(this, "Удалить сериал",
                $"Удалить «{entry.Show.name}» вместе с отметками просмотра?", confirm: true).ShowDialog() != true) return;
            entries.Remove(entry);
            string error = Save();
            if (error != null)
            {
                entries.Add(entry);
                ShowError(error);
                return;
            }
            BindLibrary(Selected == entry ? entries.FirstOrDefault() : Selected);
        }

        private string Save()
        {
            try { Library.Save(entries); return null; }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                return "Не удалось сохранить библиотеку. " + ex.Message;
            }
        }

        private void ShowError(string message)
        {
            if (message != null) new NoticeWindow(this, "Ошибка", message).ShowDialog();
        }

        private void ShowChanged(object sender, SelectionChangedEventArgs e) => RenderDetails();

        private void RenderDetails(int? preferredSeason = null)
        {
            var entry = Selected;
            UpdateRefreshSeriesButton();
            Details.Visibility = entry == null ? Visibility.Collapsed : Visibility.Visible;
            Empty.Visibility = entry == null ? Visibility.Visible : Visibility.Collapsed;
            if (entry == null) return;

            ShowTitle.Text = entry.Show.Label;
            SourceLink.Visibility = entry.Show.IsManual ? Visibility.Collapsed : Visibility.Visible;
            ShowRussianTitle.Text = entry.Show.HasRussianTitle ? "\u2002" + entry.Show.RussianTitle : string.Empty;
            ShowHeading.ToolTip = ShowTitle.Text + ShowRussianTitle.Text;
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
                        ReleaseLabel(episode.airdate),
                    Watched = entry.Watched.Contains(episode.id),
                    IsCurrent = entry.Current == episode.id,
                    IsUnreleased = !entry.IsEpisodeReleased(episode)
                }).ToList();
            Episodes.ItemsSource = rows;
            MarkSeasonButton.IsEnabled = rows.Any(row => !row.IsUnreleased && !row.Watched);
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
            ShowError(UpdateProgress(entry =>
            {
                if (box.IsChecked == true)
                {
                    var episode = entry.Episodes.FirstOrDefault(item => item.id == row.Id);
                    if (episode == null || !entry.IsEpisodeReleased(episode)) return;
                    entry.Watched.Add(row.Id);
                    if (entry.Current == row.Id) entry.Current = null;
                }
                else entry.Watched.Remove(row.Id);
            }));
        }

        private void SetCurrent(object sender, RoutedEventArgs e)
        {
            if (((Button)sender).DataContext is EpisodeRow row)
                ShowError(UpdateProgress(entry =>
                {
                    if (entry.Current == row.Id) entry.Current = null;
                    else
                    {
                        var episode = entry.Episodes.FirstOrDefault(item => item.id == row.Id);
                        if (episode != null && entry.IsEpisodeReleased(episode)) entry.Current = row.Id;
                    }
                }));
        }

        private void TogglePause(object sender, RoutedEventArgs e) => TogglePause(Selected);

        private static bool CanTogglePause(Entry entry) =>
            entry.IsPaused || entry.Episodes.Count == 0 || entry.Episodes.Any(episode => !entry.Watched.Contains(episode.id)) ||
            !string.Equals(entry.Show.status, "Ended", StringComparison.OrdinalIgnoreCase);

        private void TogglePause(Entry entry)
        {
            if (entry == null || !CanTogglePause(entry)) return;

            var selection = Selected;
            var season = (Seasons.SelectedItem as SeasonRow)?.Number;
            bool wasPaused = entry.IsPaused;
            entry.IsPaused = !wasPaused;
            string error = Save();
            if (error != null)
            {
                entry.IsPaused = wasPaused;
                ShowError(error);
                return;
            }

            BindLibrary(selection);
            if (Selected == selection) RenderDetails(season);
        }

        private void MarkSeason(object sender, RoutedEventArgs e)
        {
            if (!(Seasons.SelectedItem is SeasonRow season)) return;
            MarkSeasonWatched(Selected, season.Number);
        }

        private void MarkSeasonWatched(Entry target, int seasonNumber)
        {
            ShowError(UpdateProgress(entry =>
            {
                foreach (var episode in entry.Episodes.Where(episode => episode.season == seasonNumber && entry.IsEpisodeReleased(episode)))
                    entry.Watched.Add(episode.id);
                if (entry.Current.HasValue && entry.Watched.Contains(entry.Current.Value)) entry.Current = null;
            }, target));
        }

        private string UpdateProgress(Action<Entry> update, Entry target = null)
        {
            var entry = target ?? Selected;
            if (entry == null) return null;
            var previous = new HashSet<int>(entry.Watched);
            var current = entry.Current;
            var season = (Seasons.SelectedItem as SeasonRow)?.Number;
            var activity = entry.ActivityOrder;
            var selection = Selected;
            update(entry);
            bool changed = current != entry.Current || !previous.SetEquals(entry.Watched);
            if (changed)
                entry.ActivityOrder = entries.Max(item => item.ActivityOrder) + 1;

            string error = Save();
            if (error != null)
            {
                entry.Watched = previous;
                entry.Current = current;
                entry.ActivityOrder = activity;
            }
            else if (changed)
            {
                BindLibrary(selection);
                if (Shows.Items.Contains(entry)) Shows.ScrollIntoView(entry);
            }
            if (selection != null && Selected == selection && (entry == selection || changed)) RenderDetails(season);
            return error;
        }

        private void ThumbnailFailed(object sender, ExceptionRoutedEventArgs e) =>
            ((Image)sender).SetCurrentValue(Image.SourceProperty, null);

        private void PosterFailed(object sender, ExceptionRoutedEventArgs e) => Cover.Source = null;
        private void OpenSource(object sender, RoutedEventArgs e) => OpenLink(Selected?.Show.url);

        private void OpenLink(string link)
        {
            if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Host != "www.tvmaze.com" ||
                (uri.Scheme != "https" && uri.Scheme != "http")) return;
            try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception ex) { ShowError("Не удалось открыть ссылку. " + ex.Message); }
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
            public bool IsUnreleased { get; set; }
            public bool IsCurrent { get; set; }
            public bool CanToggleWatched => !IsUnreleased || Watched;
            public bool CanToggleCurrent => !IsUnreleased || IsCurrent;
            public string CurrentToolTip => IsCurrent ? "Снять метку «Смотрю»" : "Смотрю";
            public string CurrentLabel => CurrentToolTip + ": " + Label;
        }
    }
}

