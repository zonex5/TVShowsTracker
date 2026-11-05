using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace TVShowsTracker
{
    public partial class ManualSeriesWindow : Window
    {
        private const int MaxSeasons = 100;
        // This is also the stride of the persisted episode IDs.
        private const int MaxEpisodesPerSeason = 1000;
        private const int MaxEpisodes = 10000;
        private readonly Func<Entry, string> save;

        internal ManualSeriesWindow(Func<Entry, string> save, string title, Entry entry = null)
        {
            this.save = save;
            InitializeComponent();
            OriginalTitle.Text = title;
            if (entry != null)
            {
                Title = "Редактировать сериал";
                SaveButton.Content = "Сохранить";
                RussianTitle.Text = entry.Show.RussianTitle ?? string.Empty;
                Year.Text = entry.Show.premiered ?? string.Empty;
                EpisodeCounts.Text = string.Join(", ", entry.Seasons.OrderBy(season => season.number)
                    .Select(season => season.episodeOrder.GetValueOrDefault()));
                Ended.IsChecked = entry.Show.status == "Ended";
            }
            Loaded += (sender, args) =>
            {
                OriginalTitle.Focus();
                OriginalTitle.SelectAll();
            };
        }

        private void SaveClick(object sender, RoutedEventArgs e)
        {
            string name = OriginalTitle.Text.Trim();
            string year = Year.Text.Trim();
            string russianTitle = RussianTitle.Text.Trim();
            if (name.Length == 0)
            {
                ShowError("Введите оригинальное название.", OriginalTitle);
                return;
            }
            if (year.Length != 0 && (year.Length != 4 || !TryParseNumber(year, out int premiereYear) || premiereYear == 0))
            {
                ShowError("Укажите год четырьмя цифрами или оставьте поле пустым.", Year);
                return;
            }

            var parts = EpisodeCounts.Text.Split(',');
            if (parts.Length > MaxSeasons)
            {
                ShowError($"Укажите не более {MaxSeasons} сезонов.", EpisodeCounts);
                return;
            }
            var counts = new int[parts.Length];
            for (int index = 0; index < parts.Length; index++)
            {
                if (!TryParseNumber(parts[index].Trim(), out counts[index]) || counts[index] > MaxEpisodesPerSeason)
                {
                    ShowError($"Сезон {index + 1}: укажите число серий от 0 до {MaxEpisodesPerSeason}.", EpisodeCounts);
                    return;
                }
            }
            if (counts.Sum() > MaxEpisodes)
            {
                ShowError($"Укажите не более {MaxEpisodes} серий всего.", EpisodeCounts);
                return;
            }

            var entry = new Entry
            {
                Show = new Show
                {
                    name = name,
                    RussianTitle = russianTitle.Length > 0 && !string.Equals(name, russianTitle, StringComparison.OrdinalIgnoreCase)
                        ? russianTitle : null,
                    premiered = year.Length > 0 ? year : null,
                    status = Ended.IsChecked == true ? "Ended" : null
                }
            };
            for (int index = 0; index < counts.Length; index++)
            {
                int season = index + 1;
                entry.Seasons.Add(new Season { number = season, episodeOrder = counts[index] });
                for (int episode = 1; episode <= counts[index]; episode++)
                {
                    entry.Episodes.Add(new Episode
                    {
                        id = index * MaxEpisodesPerSeason + episode,
                        season = season,
                        number = episode,
                        name = $"Серия {episode}"
                    });
                }
            }

            string error = save(entry);
            if (error == null) DialogResult = true;
            else ShowError(error);
        }

        private static bool TryParseNumber(string text, out int value) =>
            int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);

        private void ShowError(string message, TextBox field = null)
        {
            ErrorMessage.Text = message;
            ErrorMessage.Visibility = Visibility.Visible;
            field?.Focus();
        }
    }
}
