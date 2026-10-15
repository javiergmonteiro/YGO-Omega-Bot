using BotTest.Decks;
using BotTest.Game.AI.Bots.MasterDuel.BlueEyes;
using BotTest.Game.AI.Bots.MasterDuel.Blackwings;
using BotTest.Game.AI.Bots.MasterDuel.DarkMagician;
using BotTest.Game.AI.Bots.MasterDuel.EdisonGeneric;
using BotTest.Game.AI.Bots.MasterDuel.HandTraps;
using BotTest.Game.AI.Bots.MasterDuel.SwordSoul;
using BotTest.Game.AI.Bots.RushDuel.SevensRoad;
using BotTest.Game.AI.Bots.SpeedDuel.Amazoness;
using BotTest.Game.AI.Bots.SpeedDuel.MachineAngelAscension;
using BotTest.Game.AI.Bots.SpeedDuel.SuperDuck;
using BotTest.Game.AI.Heuristics;
using OmegaBotWrapper;
using OmegaBotWrapper.Heuristics;

namespace BotTest.Web;

public static class DeckCatalog
{
    public static readonly IReadOnlyList<DeckDefinition> Definitions =
    [
        CreateBrain("MasterDuel", "Master Duel", MasterDuelDeck.BlueEyes.ToString(), DeckManager.GetDeckName(MasterDuelDeck.BlueEyes), DeckManager.GetDeckHash(MasterDuelDeck.BlueEyes), () => new BlueEyesBotBrain(new WindBotInspiredCardEvaluator(), new DefaultBoardEvaluator())),
        CreateBrain("MasterDuel", "Master Duel", MasterDuelDeck.DarkMagician.ToString(), DeckManager.GetDeckName(MasterDuelDeck.DarkMagician), DeckManager.GetDeckHash(MasterDuelDeck.DarkMagician), () => new DarkMagicianBotBrain(new WindBotInspiredCardEvaluator(), new DefaultBoardEvaluator())),
        CreateBrain("MasterDuel", "Master Duel", "DarkMagicianEdisonRules", "DarkMagicianEdison", DeckManager.GetDeckHash(MasterDuelDeck.DarkMagician), () => new EdisonGenericBotBrain(new WindBotInspiredCardEvaluator(), new DefaultBoardEvaluator())),
        CreateBrain("MasterDuel", "Master Duel", MasterDuelDeck.SwordSoul.ToString(), DeckManager.GetDeckName(MasterDuelDeck.SwordSoul), DeckManager.GetDeckHash(MasterDuelDeck.SwordSoul), () => new SwordSoulBotBrain(new WindBotInspiredCardEvaluator(), new DefaultBoardEvaluator())),
        CreateBrain("MasterDuel", "Master Duel", MasterDuelDeck.HandTraps.ToString(), DeckManager.GetDeckName(MasterDuelDeck.HandTraps), DeckManager.GetDeckHash(MasterDuelDeck.HandTraps), () => new HandTrapsBotBrain(new WindBotInspiredCardEvaluator(), new DefaultBoardEvaluator())),
        CreateBrain("MasterDuel", "Master Duel", MasterDuelDeck.Blackwings.ToString(), DeckManager.GetDeckName(MasterDuelDeck.Blackwings), DeckManager.GetDeckHash(MasterDuelDeck.Blackwings), () => new BlackwingsBotBrain(new WindBotInspiredCardEvaluator(), new DefaultBoardEvaluator())),
        CreateBrain("SpeedDuel", "Speed Duel", SpeedDuelDeck.SuperDuck.ToString(), DeckManager.GetDeckName(SpeedDuelDeck.SuperDuck), DeckManager.GetDeckHash(SpeedDuelDeck.SuperDuck), () => new SuperDuckBotBrain(new WindBotInspiredCardEvaluator(), new DefaultBoardEvaluator())),
        CreateBrain("SpeedDuel", "Speed Duel", SpeedDuelDeck.MachineAngelAscension.ToString(), DeckManager.GetDeckName(SpeedDuelDeck.MachineAngelAscension), DeckManager.GetDeckHash(SpeedDuelDeck.MachineAngelAscension), () => new MachineAngelAscensionBotBrain(new WindBotInspiredCardEvaluator(), new DefaultBoardEvaluator())),
        CreateBrain("SpeedDuel", "Speed Duel", SpeedDuelDeck.Amazoness.ToString(), DeckManager.GetDeckName(SpeedDuelDeck.Amazoness), DeckManager.GetDeckHash(SpeedDuelDeck.Amazoness), () => new AmazonessBotBrain(new WindBotInspiredCardEvaluator(), new DefaultBoardEvaluator())),
        CreateBrain("RushDuel", "Rush Duel", RushDuelDeck.SevensRoad.ToString(), DeckManager.GetDeckName(RushDuelDeck.SevensRoad), DeckManager.GetDeckHash(RushDuelDeck.SevensRoad), () => new SevensRoadBotBrain(new WindBotInspiredCardEvaluator(), new DefaultBoardEvaluator())),
        CreateBrain("RushDuel", "Rush Duel", RushDuelDeck.CustomRushDeck.ToString(), DeckManager.GetDeckName(RushDuelDeck.CustomRushDeck), DeckManager.GetDeckHash(RushDuelDeck.CustomRushDeck), () => new SevensRoadBotBrain(new WindBotInspiredCardEvaluator(), new DefaultBoardEvaluator())),
    ];

    public static IReadOnlyList<FormatOption> GetFormatOptions()
    {
        return Definitions
            .GroupBy(definition => new { definition.FormatId, definition.FormatName })
            .Select(group => new FormatOption(
                group.Key.FormatId,
                group.Key.FormatName,
                group.Select(definition => new DeckOptionDto(definition.DeckId, definition.DeckName, definition.AssetDeckId)).ToArray()))
            .ToArray();
    }

    /// <summary>
    /// Creates the bot. If the player has a deck with the same name in Omega's deck list, that list is used
    /// (so edits made in the deck editor reach the bot); otherwise the code stored in DeckManager.
    /// </summary>
    public static async Task<(DuelBotWrapper Bot, string DeckSource)> CreateBotAsync(string formatId, string deckId)
    {
        var definition = Definitions.FirstOrDefault(item =>
            item.FormatId.Equals(formatId, StringComparison.OrdinalIgnoreCase) &&
            item.DeckId.Equals(deckId, StringComparison.OrdinalIgnoreCase));

        if (definition is null)
            throw new InvalidOperationException($"No bot implementation found for format '{formatId}' and deck '{deckId}'.");

        var fromGame = await TryReadDeckFromGameAsync(definition.DeckName);
        if (fromGame.HasValue)
            return (definition.Factory(fromGame.Value.Hash), $"Mazo leído del juego: {definition.DeckName} ({fromGame.Value.Summary})");

        return (definition.Factory(definition.StoredHash), $"Usando el mazo guardado de {definition.DeckName} (no hay uno con ese nombre en Omega)");
    }

    private static async Task<(string Hash, string Summary)?> TryReadDeckFromGameAsync(string deckName)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var doc = System.Text.Json.JsonDocument.Parse(await http.GetStringAsync("http://127.0.0.1:19999/deck-list"));
            foreach (var deck in doc.RootElement.GetProperty("Decks").EnumerateArray())
            {
                if (!string.Equals(deck.GetProperty("DeckName").GetString(), deckName, StringComparison.OrdinalIgnoreCase))
                    continue;

                int[] Ids(string part) => deck.GetProperty(part).EnumerateArray().Select(id => id.GetInt32()).ToArray();
                var (main, extra, side) = (Ids("Main"), Ids("Extra"), Ids("Side"));
                return (OmegaDeckCode.Encode(main, extra, side), $"{main.Length}/{extra.Length}/{side.Length}");
            }
        }
        catch (Exception)
        {
            // Omega not reachable or unexpected format: fall back to the stored code.
        }

        return null;
    }

    private static DeckDefinition CreateBrain(
        string formatId,
        string formatName,
        string deckId,
        string deckName,
        string deckHash,
        Func<IBotBrain> brainFactory) =>
        new(formatId, formatName, deckId, deckName, deckId, deckHash, hash => new DuelBotWrapper(deckName, hash, brainFactory()));
}

public sealed record DeckDefinition(
    string FormatId,
    string FormatName,
    string DeckId,
    string DeckName,
    string AssetDeckId,
    string StoredHash,
    Func<string, DuelBotWrapper> Factory);
