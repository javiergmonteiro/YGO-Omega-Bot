using BotTest.Utils;
using OmegaBotWrapper;

namespace BotTest.Web;

public sealed class BotControlService
{
    private const int MaxLogEntries = 16;

    private readonly object _sync = new();
    private readonly IReadOnlyList<FormatOption> _formatOptions;

    private string _selectedFormat;
    private string _selectedDeckId;
    private string _selectedDeckName;
    private bool _isBusy;
    private string _status = "Ready";
    private string _statusDetail = "Choose a format and a deck to start the duel.";
    private DateTimeOffset _lastUpdated = DateTimeOffset.Now;
    private readonly List<LogEntry> _logs = new();
    private Task? _runningTask;
    private CancellationTokenSource? _cts;
    private readonly SessionLog _sessionLog;

    public BotControlService(SessionLog sessionLog)
    {
        _sessionLog = sessionLog;
        _formatOptions = DeckCatalog.GetFormatOptions();

        var defaultFormat = _formatOptions.First();
        var defaultDeck = defaultFormat.Decks.First();

        _selectedFormat = defaultFormat.Id;
        _selectedDeckId = defaultDeck.Id;
        _selectedDeckName = defaultDeck.Name;

        AddLog("Panel initialized.", "system");
    }

    public OptionsResponse GetOptions() => new(_formatOptions, _selectedFormat, _selectedDeckId);

    public BotStateResponse GetState()
    {
        lock (_sync)
        {
            return new BotStateResponse(
                _selectedFormat,
                _selectedDeckId,
                _selectedDeckName,
                _isBusy,
                _status,
                _statusDetail,
                _lastUpdated,
                _logs.ToArray());
        }
    }

    public CommandResult UpdateSelection(string format, string deckId)
    {
        lock (_sync)
        {
            if (_isBusy)
            {
                return CommandResult.Fail("Wait for the current match to finish before changing your selection.");
            }

            var formatOption = _formatOptions.FirstOrDefault(option => option.Id.Equals(format, StringComparison.OrdinalIgnoreCase));
            if (formatOption is null)
            {
                return CommandResult.Fail("Invalid format.");
            }

            var deckOption = formatOption.Decks.FirstOrDefault(option => option.Id.Equals(deckId, StringComparison.OrdinalIgnoreCase));
            if (deckOption is null)
            {
                return CommandResult.Fail("Invalid deck for the selected format.");
            }

            _selectedFormat = formatOption.Id;
            _selectedDeckId = deckOption.Id;
            _selectedDeckName = deckOption.Name;
            _status = "Ready";
            _statusDetail = $"Selection ready: {deckOption.Name} in {formatOption.Name}.";
            _lastUpdated = DateTimeOffset.Now;

            AddLog($"Selected deck: {deckOption.Name} ({formatOption.Name}).", "system");

            return CommandResult.Ok("Updated selection.");
        }
    }

    public CommandResult StartDuel()
    {
        DeckSelection selection;

        lock (_sync)
        {
            if (_isBusy)
            {
                return CommandResult.Fail("A duel is already underway.");
            }

            selection = new DeckSelection(_selectedFormat, _selectedDeckId, _selectedDeckName);
            _isBusy = true;
            _cts = new CancellationTokenSource();
            _status = "Connecting";
            _statusDetail = $"Connecting the bot to the deck {selection.DeckName}...";
            _lastUpdated = DateTimeOffset.Now;
            AddLog($"Starting the duel with {selection.DeckName}.", "system");
            _sessionLog.StartSession(selection.DeckName);
            var token = _cts.Token;
            _runningTask = Task.Run(() => ExecuteDuelAsync(selection, requestRematch: false, token));
        }

        return CommandResult.Ok("Duel started.");
    }

    public CommandResult RequestRematch()
    {
        DeckSelection selection;

        lock (_sync)
        {
            if (_isBusy)
            {
                return CommandResult.Fail("Wait for the current flow to finish before requesting a rematch.");
            }

            selection = new DeckSelection(_selectedFormat, _selectedDeckId, _selectedDeckName);
            _isBusy = true;
            _cts = new CancellationTokenSource();
            _status = "Rematch";
            _statusDetail = "Requesting rematch and preparing a new connection...";
            _lastUpdated = DateTimeOffset.Now;
            AddLog("Requesting rematch.", "system");
            var rematchToken = _cts.Token;
            _runningTask = Task.Run(() => ExecuteDuelAsync(selection, requestRematch: true, rematchToken));
        }

        return CommandResult.Ok("Rematch requested.");
    }

    public CommandResult ResetSelection()
    {
        lock (_sync)
        {
            if (_isBusy)
            {
                return CommandResult.Fail("Cannot reset while a duel is in progress.");
            }

            _status = "Ready";
            _statusDetail = "Selection kept. You can switch decks or start a new duel.";
            _lastUpdated = DateTimeOffset.Now;
            AddLog("Panel ready for a new duel.", "system");

            return CommandResult.Ok("Panel reset.");
        }
    }

    public CommandResult ClearRecentEvents()
    {
        lock (_sync)
        {
            _logs.Clear();
            _lastUpdated = DateTimeOffset.Now;
            return CommandResult.Ok("Recent events cleared.");
        }
    }

    private async Task ExecuteDuelAsync(DeckSelection selection, bool requestRematch, CancellationToken ct = default)
    {
        void OnMessageLogged(string message) => AddLog(message, "engine");

        ConsoleUtils.MessageLogged += OnMessageLogged;

        try
        {
            if (requestRematch)
            {
                UpdateStatus("Rematch", "Sending the rematch request to Omega...");
                await DuelBotWrapper.RematchDuel();
                AddLog("Rematch request sent.", "system");
                await Task.Delay(500, ct);
            }

            var (bot, deckSource) = await DeckCatalog.CreateBotAsync(selection.FormatId, selection.DeckId);
            AddLog(deckSource, "system");

            UpdateStatus("Connecting", $"Trying to connect the bot using {selection.DeckName}...");
            var connected = await bot.Connect(ct: ct);

            if (!connected)
            {
                UpdateStatus("Connection failed", "No bot slot is available or Omega did not respond as expected.");
                AddLog("Failed to connect to the bot.", "error");
                return;
            }

            UpdateStatus("Dueling", $"Bot is dueling with {selection.DeckName}. Follow the match in Omega.");
            AddLog("Bot connected successfully.", "system");

            await bot.Start(ct);

            UpdateStatus("Finished", "The duel is over. You can request a rematch or start another deck.");
            AddLog("Duel finished.", "system");
        }
        catch (Exception ex)
        {
            UpdateStatus("Error", $"An error occurred during execution: {ex.Message}");
            AddLog($"Error: {ex.Message}", "error");
        }
        finally
        {
            ConsoleUtils.MessageLogged -= OnMessageLogged;

            lock (_sync)
            {
                _isBusy = false;
                _runningTask = null;
                _cts?.Dispose();
                _cts = null;
                _lastUpdated = DateTimeOffset.Now;
            }
        }
    }

    public CommandResult StopDuel()
    {
        lock (_sync)
        {
            if (!_isBusy || _cts == null)
                return CommandResult.Fail("No duel is currently running.");
            _cts.Cancel();
            AddLog("Stop requested.", "system");
            return CommandResult.Ok("Stop signal sent to the bot.");
        }
    }

    private void UpdateStatus(string status, string detail)
    {
        lock (_sync)
        {
            _status = status;
            _statusDetail = detail;
            _lastUpdated = DateTimeOffset.Now;
        }
    }

    private void AddLog(string message, string kind = "info")
    {
        // Engine messages already reach the session log through the console; forward only panel events.
        if (kind is "system" or "error")
            _sessionLog.Add(message, kind == "error" ? "error" : "panel");

        lock (_sync)
        {
            _logs.Insert(0, new LogEntry(DateTimeOffset.Now, message, kind));
            if (_logs.Count > MaxLogEntries)
            {
                _logs.RemoveRange(MaxLogEntries, _logs.Count - MaxLogEntries);
            }
        }
    }
}

public sealed record SelectionRequest(string Format, string DeckId);

public sealed record CommandResult(bool Success, string Message)
{
    public static CommandResult Ok(string message) => new(true, message);

    public static CommandResult Fail(string message) => new(false, message);
}

public sealed record OptionsResponse(
    IReadOnlyList<FormatOption> Formats,
    string SelectedFormat,
    string SelectedDeckId);

public sealed record BotStateResponse(
    string SelectedFormat,
    string SelectedDeckId,
    string SelectedDeckName,
    bool IsBusy,
    string Status,
    string StatusDetail,
    DateTimeOffset LastUpdated,
    IReadOnlyList<LogEntry> Logs);

public sealed record FormatOption(string Id, string Name, IReadOnlyList<DeckOptionDto> Decks);

public sealed record DeckOptionDto(string Id, string Name, string? AssetDeckId = null);

public sealed record LogEntry(DateTimeOffset Timestamp, string Message, string Kind);

internal sealed record DeckSelection(string FormatId, string DeckId, string DeckName);

public sealed record AutoConnectRequest(bool Enabled);

public sealed record NoteRequest(string Text);
