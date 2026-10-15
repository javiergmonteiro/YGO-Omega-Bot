using System.Text.Json;
using System.Text.RegularExpressions;

namespace BotTest.Web;

/// <summary>
/// Polls the Omega API once a second to report what's going on (game open? bot in the room? ready? dueling?),
/// auto-connects the selected deck when a custom bot is waiting for one, and saves each game's duel log.
/// </summary>
public sealed class GameMonitor : BackgroundService
{
    private const string OmegaApi = "http://127.0.0.1:19999/";
    private const int WaitingForDeck = 19;

    private readonly BotControlService _control;
    private readonly SessionLog _log;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private readonly object _sync = new();

    private GameStatus _status = GameStatus.Offline;
    private int _lastTurn = -1;
    private int _game = 0;
    private DateTimeOffset _lastDuelLogSave = DateTimeOffset.MinValue;

    public GameMonitor(BotControlService control, SessionLog log)
    {
        _control = control;
        _log = log;
    }

    public bool AutoConnect { get; set; } = true;

    public GameStatus GetStatus()
    {
        lock (_sync)
            return _status;
    }

    public int CurrentTurn => GetStatus().Turn;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollAsync();
            }
            catch (Exception ex)
            {
                _log.Add($"Monitor: {ex.Message}", "error");
            }

            await Task.Delay(1000, stoppingToken);
        }
    }

    private async Task PollAsync()
    {
        var control = _control.GetState();
        using var botList = await GetJsonAsync("bot-list");
        if (botList == null)
        {
            SetStatus(new GameStatus(false, false, null, null, false, 0, control.IsBusy, control.Status, "Omega no está abierto (o su API no responde).", "rojo"));
            return;
        }

        var bots = botList.RootElement.GetProperty("Bots");
        if (bots.GetArrayLength() == 0)
        {
            ResetGameTracking();
            SetStatus(new GameStatus(true, false, null, null, false, 0, control.IsBusy, control.Status,
                "No hay bot en la sala. Agregá un AI_CUSTOM en Local > Host.", "gris"));
            return;
        }

        var bot = bots[0];
        string deckName = Uri.UnescapeDataString(bot.GetProperty("DeckName").GetString() ?? "");
        int botId = bot.GetProperty("BotID").GetInt32();

        using var botState = await GetJsonAsync($"get-bot-state/{botId}");
        if (botState == null)
        {
            SetStatus(new GameStatus(true, true, deckName, null, false, 0, control.IsBusy, control.Status,
                "El bot de la sala no es custom (probablemente sea KurAI). Usá AI_CUSTOM.", "naranja"));
            return;
        }

        var header = botState.RootElement.GetProperty("CurrentStateHeader");
        int stateCode = header.GetProperty("CurrentStateCode").GetInt32();
        string stateName = header.GetProperty("CurrentStateName").GetString() ?? "";
        int turn = header.GetProperty("CurrentTurn").GetInt32();
        bool isReady = botState.RootElement.GetProperty("CurrentStateData").TryGetProperty("IsReady", out var ready) && ready.GetBoolean();

        if (stateCode == WaitingForDeck && !isReady)
        {
            if (AutoConnect && !control.IsBusy)
            {
                _log.Add("Bot esperando mazo: conectando automáticamente.", "panel");
                _control.StartDuel();
            }

            SetStatus(new GameStatus(true, true, deckName, stateName, false, turn, control.IsBusy, control.Status,
                AutoConnect ? "Bot esperando mazo: conectando..." : "Bot esperando mazo. Elegí uno y tocá Conectar.", "amarillo"));
            return;
        }

        if (!control.IsBusy)
        {
            SetStatus(new GameStatus(true, true, deckName, stateName, isReady, turn, false, control.Status,
                "Hay un bot listo pero este panel no lo controla (¿se reinició el panel?). Sacalo y volvé a agregar AI_CUSTOM.", "naranja"));
            return;
        }

        await SelectBanlistAsync();
        await TrackGameAsync(turn);

        string message = turn == 0
            ? $"Conectado con {deckName}: listo para empezar el duelo."
            : $"Jugando con {deckName} — partida {_game}, turno {turn}.";
        SetStatus(new GameStatus(true, true, deckName, stateName, isReady, turn, true, control.Status, message, "verde"));
    }

    // The room tells which banlist it uses only as a hash: map it to one of our lists (Banlists/rooms.json).
    private async Task SelectBanlistAsync()
    {
        using var room = await GetJsonAsync("room-info");
        if (room == null || !room.RootElement.TryGetProperty("LFListHash", out var hash))
            return;

        string? message = BotTest.Game.AI.Knowledge.Banlist.SelectForRoom(hash.GetInt64());
        if (message != null)
            _log.Add(message, "panel");
    }

    // Detects game changes inside a match (turn goes back) and saves the duel log every few seconds.
    private async Task TrackGameAsync(int turn)
    {
        if (turn <= 0)
            return;

        if (_game == 0 || turn < _lastTurn)
        {
            _game++;
            _log.Add($"=== Partida {_game} ===", "panel");
        }

        _lastTurn = turn;

        if (DateTimeOffset.Now - _lastDuelLogSave < TimeSpan.FromSeconds(3))
            return;

        using var duelLog = await GetJsonAsync("get-duel-log");
        if (duelLog != null && duelLog.RootElement.TryGetProperty("Log", out var text))
        {
            string plain = Regex.Replace(text.GetString() ?? "", "<[^>]+>", "");
            _log.SaveDuelLog(_game, plain);
            _lastDuelLogSave = DateTimeOffset.Now;
        }
    }

    private void ResetGameTracking()
    {
        _game = 0;
        _lastTurn = -1;
    }

    private void SetStatus(GameStatus status)
    {
        lock (_sync)
            _status = status;
    }

    private async Task<JsonDocument?> GetJsonAsync(string path)
    {
        try
        {
            using var response = await _http.GetAsync(OmegaApi + path);
            if (!response.IsSuccessStatusCode)
                return null;
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        }
        catch (Exception)
        {
            return null;
        }
    }
}

public sealed record GameStatus(
    bool GameOnline,
    bool BotInRoom,
    string? BotDeck,
    string? BotState,
    bool BotReady,
    int Turn,
    bool Connected,
    string PanelStatus,
    string Message,
    string Color)
{
    public static GameStatus Offline { get; } =
        new(false, false, null, null, false, 0, false, "", "Iniciando...", "gris");
}
