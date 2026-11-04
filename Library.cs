using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Net.Http;
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
        [IgnoreDataMember]
        public string Label => string.IsNullOrEmpty(premiered) ? name : $"{name} ({premiered.Split('-')[0]})";
    }

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

    internal static class Catalog
    {
        private static readonly HttpClient Client = new HttpClient
        {
            BaseAddress = new Uri("https://api.tvmaze.com/"),
            Timeout = TimeSpan.FromSeconds(15)
        };

        private static async Task<T> Get<T>(string route)
        {
            for (int attempt = 0; ; attempt++)
            {
                using (var response = await Client.GetAsync(route))
                {
                    if ((int)response.StatusCode == 429 && attempt == 0)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(10));
                        continue;
                    }
                    response.EnsureSuccessStatusCode();
                    using (var stream = await response.Content.ReadAsStreamAsync())
                        return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream);
                }
            }
        }

        public static Task<List<SearchResult>> Search(string query) =>
            Get<List<SearchResult>>("search/shows?q=" + Uri.EscapeDataString(query));

        public static async Task<Entry> Load(int id)
        {
            return new Entry
            {
                Show = await Get<Show>($"shows/{id}"),
                Seasons = await Get<List<Season>>($"shows/{id}/seasons"),
                Episodes = await Get<List<Episode>>($"shows/{id}/episodes")
            };
        }
    }

    internal static class Library
    {
        private static readonly string Folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SeriesDesk");
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


