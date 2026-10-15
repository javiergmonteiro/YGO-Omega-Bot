using System.Text.Json;

namespace BotTest.Game.AI.Knowledge
{
    /// <summary>
    /// Card limits of the format being played (0 forbidden, 1 limited, 2 semi-limited, 3 otherwise).
    /// Lists live in BotTest/Banlists/*.json; rooms.json maps the room's LFListHash (from /room-info) to a list,
    /// so the right one is picked per room. Unknown hashes fall back to the default list.
    /// </summary>
    public static class Banlist
    {
        private static readonly object Sync = new();
        private static string _directory = "Banlists";
        private static Dictionary<int, int> _limits = new();
        private static Dictionary<string, string> _rooms = new();
        private static string _default = "edison";
        private static long? _currentHash;

        public static string Name { get; private set; } = "(none)";

        public static void Load(string directory)
        {
            _directory = directory;
            string roomsPath = Path.Combine(directory, "rooms.json");
            if (File.Exists(roomsPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(roomsPath));
                if (doc.RootElement.TryGetProperty("default", out var def))
                    _default = def.GetString() ?? _default;
                if (doc.RootElement.TryGetProperty("rooms", out var rooms))
                    _rooms = rooms.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? _default);
            }

            LoadList(_default);
        }

        /// <summary>Picks the list for the room's LFListHash. Returns a message when the selection changed.</summary>
        public static string? SelectForRoom(long hash)
        {
            lock (Sync)
            {
                if (_currentHash == hash)
                    return null;
                _currentHash = hash;
            }

            if (_rooms.TryGetValue(hash.ToString(), out var list))
            {
                LoadList(list);
                return $"Banlist: {Name} (room hash {hash})";
            }

            LoadList(_default);
            return $"Banlist: unknown room hash {hash}, using {Name}. Add it to Banlists/rooms.json if this room uses another format.";
        }

        public static int MaxCopies(int cardId)
        {
            int canonical = CardIdentity.Canonical(cardId);
            lock (Sync)
                return _limits.TryGetValue(canonical, out int limit) ? limit : 3;
        }

        public static IReadOnlyCollection<int> CardIds
        {
            get { lock (Sync) return _limits.Keys.ToList(); }
        }

        private static void LoadList(string list)
        {
            string path = Path.Combine(_directory, list + ".json");
            if (!File.Exists(path))
                return;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var limits = doc.RootElement.GetProperty("limits").EnumerateObject()
                .ToDictionary(p => int.Parse(p.Name), p => p.Value.GetInt32());
            string name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? list : list;

            lock (Sync)
            {
                _limits = limits;
                Name = name;
            }
        }
    }

    /// <summary>
    /// What the opponent can still have: the format's limit minus the copies already seen in public zones
    /// (GY, banished, face-up on the field). E.g. once their Mirror Force (limited) is in the GY, there's no other.
    /// </summary>
    public static class CardCounter
    {
        public static int OpponentCopiesLeft(int cardId, int[]? grave, int[]? banished, IEnumerable<int> faceUpOnField)
        {
            int seen = (grave ?? Array.Empty<int>()).Count(id => CardIdentity.Same(id, cardId))
                + (banished ?? Array.Empty<int>()).Count(id => CardIdentity.Same(id, cardId))
                + faceUpOnField.Count(id => CardIdentity.Same(id, cardId));
            return Math.Max(0, Banlist.MaxCopies(cardId) - seen);
        }
    }
}
