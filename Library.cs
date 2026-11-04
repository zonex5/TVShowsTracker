using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LiteDB;


namespace TVShowsTracker
{
    public sealed class Show
    {
        public int id { get; set; }
        public string name { get; set; }
        public string premiered { get; set; }
        public string status { get; set; }
        public string url { get; set; }
        public Poster image { get; set; }
        [BsonIgnore]
        public string language { get; set; }
        [BsonIgnore]
        public ShowDetails _embedded { get; set; }
        [IgnoreDataMember]
        public string RussianTitle { get; set; }
        [BsonIgnore, IgnoreDataMember]
        public bool HasRussianTitle => !string.IsNullOrWhiteSpace(RussianTitle);
        [IgnoreDataMember]
        public string Label => string.IsNullOrEmpty(premiered) ? name : $"{name} ({premiered.Split('-')[0]})";
    }

    public sealed class ShowDetails { public List<ShowAlias> akas { get; set; } }
    public sealed class ShowAlias { public string name { get; set; } public AliasCountry country { get; set; } }
    public sealed class AliasCountry { public string code { get; set; } }
    public sealed class Poster { public string medium { get; set; } }
    public sealed class Season { public int number { get; set; } public int? episodeOrder { get; set; } }
    public sealed class Episode
    {
        public int id { get; set; }
        public int season { get; set; }
        public int? number { get; set; }
        public string name { get; set; }
        public string airdate { get; set; }
    }

    public sealed class Entry
    {
        public Show Show { get; set; }
        public List<Season> Seasons { get; set; } = new List<Season>();
        public List<Episode> Episodes { get; set; } = new List<Episode>();
        public HashSet<int> Watched { get; set; } = new HashSet<int>();
        public int? Current { get; set; }
        public long ActivityOrder { get; set; }
        public bool IsPaused { get; set; }

        [BsonIgnore]
        public bool HasUnwatchedEpisodes => Episodes.Any(episode => !Watched.Contains(episode.id));

        [BsonIgnore]
        public bool ShowUnwatchedIndicator => !IsPaused && HasUnwatchedEpisodes;
    }

    public sealed class SearchResult { public Show show { get; set; } }

    internal sealed class CatalogUnavailableException : Exception
    {
        public CatalogUnavailableException(string message, Exception inner = null) : base(message, inner) { }
    }

    internal static class Catalog
    {
        private static readonly Regex RussianLetters = new Regex(@"[А-Яа-яЁё]");
        private static readonly HttpClient Client = new HttpClient
        {
            BaseAddress = new Uri("https://api.tvmaze.com/"),
            Timeout = TimeSpan.FromSeconds(15)
        };

        private static async Task<T> Get<T>(string route, CancellationToken cancellationToken)
        {
            for (int attempt = 0; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                HttpResponseMessage response;
                try
                {
                    response = await Client.GetAsync(route, cancellationToken).ConfigureAwait(false);
                }
                catch (HttpRequestException ex)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new CatalogUnavailableException("Не удалось подключиться к TVmaze.", ex);
                }
                catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new CatalogUnavailableException("TVmaze не ответил вовремя.", ex);
                }

                using (response)
                {
                    int status = (int)response.StatusCode;
                    if (status == 429 && attempt == 0)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    if (status == 429)
                        throw new CatalogUnavailableException("TVmaze ограничил частоту запросов. Повторите обновление позже.");
                    if (status >= 500)
                        throw new CatalogUnavailableException($"TVmaze временно недоступен (HTTP {status}).");
                    response.EnsureSuccessStatusCode();
                    using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream);
                }
            }
        }

        public static Task<List<SearchResult>> Search(string query) =>
            Get<List<SearchResult>>("search/shows?q=" + Uri.EscapeDataString(query), CancellationToken.None);

        public static async Task<Entry> Load(int id, CancellationToken cancellationToken = default, string savedRussianTitle = null)
        {
            var show = await Get<Show>($"shows/{id}?embed=akas", cancellationToken).ConfigureAwait(false);
            show.RussianTitle = FindRussianTitle(show, savedRussianTitle);
            return new Entry
            {
                Show = show,
                Seasons = await Get<List<Season>>($"shows/{id}/seasons", cancellationToken).ConfigureAwait(false),
                Episodes = await Get<List<Episode>>($"shows/{id}/episodes", cancellationToken).ConfigureAwait(false)
            };
        }

        private static string FindRussianTitle(Show show, string savedRussianTitle)
        {
            var title = show.name?.Trim();
            if (string.Equals(show.language, "Russian", StringComparison.OrdinalIgnoreCase) &&
                RussianLetters.IsMatch(title ?? string.Empty)) return null;

            var aliases = show._embedded?.akas;
            var russianTitle = aliases == null ? savedRussianTitle : aliases
                .Where(alias => alias?.country?.code == "RU")
                .Select(alias => alias.name?.Trim())
                .FirstOrDefault(alias => alias != null && RussianLetters.IsMatch(alias));
            return string.Equals(title, russianTitle, StringComparison.OrdinalIgnoreCase) ? null : russianTitle;
        }
    }

    internal static class Library
    {
        private static readonly string Folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TVShowsTracker");
        private static readonly string FileName = Path.Combine(Folder, "library.db");
        private static readonly BsonMapper Mapper = new BsonMapper
        {
            TrimWhitespace = false,
            EmptyStringToNull = false
        };

        private static LiteDatabase Open() =>
            new LiteDatabase(new ConnectionString { Filename = FileName }, Mapper);

        public static List<Entry> Read()
        {
            if (!File.Exists(FileName))
                return new List<Entry>();

            using (var db = Open())
            {
                var entries = new List<Entry>();
                foreach (var document in db.GetCollection("entries").FindAll())
                    entries.Add(Mapper.ToObject<Entry>(document));
                return entries;
            }
        }

        public static void Save(List<Entry> entries)
        {
            Directory.CreateDirectory(Folder);
            using (var db = Open())
                Write(db, entries);
        }

        private static void Write(LiteDatabase db, List<Entry> entries)
        {
            db.BeginTrans();
            try
            {
                var collection = db.GetCollection("entries");
                collection.DeleteAll();
                foreach (var entry in entries)
                {
                    var document = Mapper.ToDocument(entry);
                    document["_id"] = entry.Show.id;
                    collection.Insert(document);
                }
                db.Commit();
            }
            catch
            {
                db.Rollback();
                throw;
            }
        }
    }
}


