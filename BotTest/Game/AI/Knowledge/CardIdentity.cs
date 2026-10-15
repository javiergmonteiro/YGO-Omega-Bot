using System.Reflection;
using System.Text.Json;
using OmegaBotWrapper.Struct;

namespace BotTest.Game.AI.Knowledge
{
    /// <summary>
    /// Omega has format-specific versions of some cards (errata'd to the ruling of the format, e.g. Edison) under
    /// their own card ID and with no alias (e.g. Quickdraw Synchron 20932152 / 20932172), so every table keyed by ID
    /// would miss them. For our rules they're the same card (what it means), while Omega resolves its exact text. This maps any ID to the "known" ID with the same name
    /// (the IDs in <see cref="CardKnowledge"/>, deck card-ID classes and the banlist), looking names up once
    /// through /card-info and caching them.
    /// </summary>
    public static class CardIdentity
    {
        private const string CardInfoUrl = "http://127.0.0.1:19999/card-info/";
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };
        private static readonly SemaphoreSlim Lock = new(1, 1);
        private static readonly Dictionary<int, int> Canonicals = new();
        private static readonly HashSet<int> KnownIds = new();
        private static Dictionary<string, int>? _knownByName;
        private static readonly Dictionary<int, string> Names = new();

        /// <summary>Card name if it was looked up before (every ID seen in a decision is).</summary>
        public static string? NameOf(int id)
        {
            lock (Canonicals)
                return Names.TryGetValue(id, out var name) ? name : null;
        }

        /// <summary>Registers IDs our rules refer to (public const ints, int[] fields and dictionary keys of a type).</summary>
        public static void RegisterKnownIds(Type type)
        {
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                switch (field.GetValue(null))
                {
                    case int id when id > 100000:
                        KnownIds.Add(id);
                        break;
                    case int[] ids:
                        KnownIds.UnionWith(ids);
                        break;
                    case IReadOnlyDictionary<int, int> map:
                        KnownIds.UnionWith(map.Keys);
                        break;
                }
            }
        }

        public static void RegisterKnownIds(IEnumerable<int> ids) => KnownIds.UnionWith(ids);

        /// <summary>The known ID for this card ID (itself if it isn't an alternate art of a known card).</summary>
        public static int Canonical(int id)
        {
            lock (Canonicals)
                return Canonicals.TryGetValue(id, out int canonical) ? canonical : id;
        }

        /// <summary>Same card, ignoring alternate artworks.</summary>
        public static bool Same(int a, int b) => a == b || Canonical(a) == Canonical(b);

        /// <summary>Marks an alternate artwork with its known ID as alias, so IsCode(...) matches it.</summary>
        public static void Normalize(DynamicCard[]? cards)
        {
            if (cards == null)
                return;
            for (int i = 0; i < cards.Length; i++)
            {
                int canonical = Canonical(cards[i].ID);
                if (canonical != cards[i].ID && cards[i].Alias == 0)
                    cards[i].Alias = canonical;
            }
        }

        public static List<DynamicCard> Normalize(List<DynamicCard> cards)
        {
            var array = cards.ToArray();
            Normalize(array);
            return array.ToList();
        }

        /// <summary>Resolves any IDs not seen before (one /card-info batch).</summary>
        public static async Task EnsureAsync(IEnumerable<int> ids)
        {
            List<int> unknown;
            lock (Canonicals)
                unknown = ids.Where(id => id > 0 && !Canonicals.ContainsKey(id)).Distinct().ToList();
            if (unknown.Count == 0)
                return;

            await Lock.WaitAsync();
            try
            {
                if (_knownByName == null)
                {
                    _knownByName = new Dictionary<string, int>();
                    foreach (var (id, name) in await FetchNamesAsync(KnownIds))
                    {
                        _knownByName.TryAdd(name, id);
                        lock (Canonicals)
                            Names[id] = name;
                    }
                }
                var names = await FetchNamesAsync(unknown);
                lock (Canonicals)
                {
                    foreach (var (id, name) in names)
                        Names[id] = name;
                    foreach (int id in unknown)
                    {
                        int canonical = KnownIds.Contains(id) ? id
                            : names.TryGetValue(id, out var name) && _knownByName.TryGetValue(name, out int known) ? known
                            : id;
                        Canonicals[id] = canonical;
                    }
                }
            }
            catch (Exception)
            {
                // Omega not reachable: treat the IDs as themselves for now (retried next time).
            }
            finally
            {
                Lock.Release();
            }
        }

        private static async Task<Dictionary<int, string>> FetchNamesAsync(IEnumerable<int> ids)
        {
            var result = new Dictionary<int, string>();
            foreach (var chunk in ids.Where(id => id > 0).Distinct().Chunk(60))
            {
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(CardInfoUrl + string.Join(",", chunk)));
                foreach (var card in doc.RootElement.GetProperty("Cards").EnumerateArray())
                {
                    string? name = card.GetProperty("Name").GetString();
                    if (name != null)
                        result[card.GetProperty("ID").GetInt32()] = name;
                }
            }
            return result;
        }
    }
}
