using BotTest.Web;

var builder = WebApplication.CreateBuilder(args);

// Only the bot's own output goes to the console (and from there to the session log).
builder.Logging.ClearProviders();

BotTest.Game.AI.Knowledge.Banlist.Load(Path.Combine(builder.Environment.ContentRootPath, "Banlists"));
// IDs our rules know, so alternate artworks (own IDs in Omega, no alias) can be matched to them by name.
BotTest.Game.AI.Knowledge.CardIdentity.RegisterKnownIds(typeof(BotTest.Game.AI.Knowledge.CardKnowledge));
BotTest.Game.AI.Knowledge.CardIdentity.RegisterKnownIds(typeof(BotTest.Game.AI.Bots.MasterDuel.Blackwings.BlackwingsCardId));
BotTest.Game.AI.Knowledge.CardIdentity.RegisterKnownIds(BotTest.Game.AI.Knowledge.Banlist.CardIds);

// Logs folder: --logs <path> (the shareable package uses its own "logs" folder), default <repo>/logs.
var logsDirectory = Path.GetFullPath(builder.Configuration["logs"] ?? Path.Combine(builder.Environment.ContentRootPath, "..", "logs"));
var sessionLog = new SessionLog(logsDirectory);
Console.SetOut(new ConsoleTee(Console.Out, sessionLog));

builder.Services.AddSingleton(sessionLog);
builder.Services.AddSingleton<BotControlService>();
builder.Services.AddSingleton<GameMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<GameMonitor>());

var app = builder.Build();

// ---- Simple control page (control.html) ----

app.MapGet("/api/control/status", (GameMonitor monitor, BotControlService control) =>
{
    var state = control.GetState();
    return Results.Ok(new
    {
        Game = monitor.GetStatus(),
        monitor.AutoConnect,
        Banlist = BotTest.Game.AI.Knowledge.Banlist.Name,
        state.SelectedFormat,
        state.SelectedDeckId,
        state.SelectedDeckName,
    });
});

app.MapGet("/api/control/decks", (BotControlService control) =>
{
    var decks = control.GetOptions().Formats
        .SelectMany(format => format.Decks.Select(deck => new { FormatId = format.Id, FormatName = format.Name, deck.Id, deck.Name }));
    return Results.Ok(decks);
});

app.MapPost("/api/control/connect", (SelectionRequest request, BotControlService control) =>
{
    var selection = control.UpdateSelection(request.Format, request.DeckId);
    if (!selection.Success)
        return Results.BadRequest(selection);

    var result = control.StartDuel();
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
});

app.MapPost("/api/control/select", (SelectionRequest request, BotControlService control) =>
{
    var result = control.UpdateSelection(request.Format, request.DeckId);
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
});

app.MapPost("/api/control/disconnect", (BotControlService control) =>
{
    var result = control.StopDuel();
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
});

app.MapPost("/api/control/autoconnect", (AutoConnectRequest request, GameMonitor monitor) =>
{
    monitor.AutoConnect = request.Enabled;
    return Results.Ok(new { monitor.AutoConnect });
});

app.MapGet("/api/control/log", (long? since, SessionLog log) => Results.Ok(log.GetSince(since ?? 0)));

app.MapPost("/api/control/note", (NoteRequest request, SessionLog log, GameMonitor monitor) =>
{
    if (string.IsNullOrWhiteSpace(request.Text))
        return Results.BadRequest(new { Message = "La nota está vacía." });

    log.AddNote(request.Text.Trim(), monitor.CurrentTurn);
    return Results.Ok(new { Message = "Nota guardada." });
});

app.MapPost("/api/control/open-logs", (SessionLog log) =>
{
    string folder = log.CurrentDirectory ?? log.BaseDirectory;
    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    return Results.Ok(new { Folder = folder });
});

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/options", (BotControlService controlService) =>
{
    return Results.Ok(controlService.GetOptions());
});

app.MapGet("/api/state", (BotControlService controlService) =>
{
    return Results.Ok(controlService.GetState());
});

app.MapPost("/api/selection", (SelectionRequest request, BotControlService controlService) =>
{
    var result = controlService.UpdateSelection(request.Format, request.DeckId);
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
});

app.MapPost("/api/duel/start", (BotControlService controlService) =>
{
    var result = controlService.StartDuel();
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
});

app.MapPost("/api/duel/rematch", (BotControlService controlService) =>
{
    var result = controlService.RequestRematch();
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
});

app.MapPost("/api/duel/reset", (BotControlService controlService) =>
{
    var result = controlService.ResetSelection();
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
});

app.MapPost("/api/logs/clear", (BotControlService controlService) =>
{
    var result = controlService.ClearRecentEvents();
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
});

app.MapFallbackToFile("index.html");

app.Run();
