using System.Text;
using System.Text.RegularExpressions;

namespace BotTest.Web;

/// <summary>
/// Everything the bot prints, kept in memory for the control page and saved to disk for feedback.
/// One folder per bot connection under logs/: decisiones.log (bot decisions + notes),
/// duelo-gN.txt (the game's duel log per game) and notas.txt (feedback notes only).
/// </summary>
public sealed class SessionLog
{
    private const int MaxLines = 2000;

    private readonly object _sync = new();
    private readonly List<LogLine> _lines = new();
    private readonly string _baseDirectory;
    private long _nextId = 1;
    private StreamWriter? _writer;

    public SessionLog(string baseDirectory)
    {
        _baseDirectory = baseDirectory;
        Directory.CreateDirectory(_baseDirectory);
    }

    public string BaseDirectory => _baseDirectory;

    public string? CurrentDirectory { get; private set; }

    public void StartSession(string deckName)
    {
        lock (_sync)
        {
            _writer?.Dispose();
            string safeDeck = Regex.Replace(deckName, @"[^\w\-]+", "_");
            CurrentDirectory = Path.Combine(_baseDirectory, $"{DateTime.Now:yyyy-MM-dd_HHmmss}_{safeDeck}");
            Directory.CreateDirectory(CurrentDirectory);
            _writer = new StreamWriter(Path.Combine(CurrentDirectory, "decisiones.log"), append: true, Encoding.UTF8) { AutoFlush = true };
        }

        Add($"=== Nueva sesión: {deckName} ===", "panel");
    }

    public void Add(string text, string kind)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        lock (_sync)
        {
            var line = new LogLine(_nextId++, DateTimeOffset.Now, text.TrimEnd(), kind);
            _lines.Add(line);
            if (_lines.Count > MaxLines)
                _lines.RemoveRange(0, _lines.Count - MaxLines);

            _writer?.WriteLine($"{line.Timestamp:HH:mm:ss} [{kind}] {line.Text}");
        }
    }

    public void AddNote(string note, int turn)
    {
        string text = $"NOTA (turno {turn}): {note}";
        Add(text, "nota");

        lock (_sync)
        {
            if (CurrentDirectory != null)
                File.AppendAllText(Path.Combine(CurrentDirectory, "notas.txt"), $"{DateTime.Now:HH:mm:ss} {text}{Environment.NewLine}", Encoding.UTF8);
        }
    }

    public void SaveDuelLog(int game, string plainText)
    {
        lock (_sync)
        {
            if (CurrentDirectory != null)
                File.WriteAllText(Path.Combine(CurrentDirectory, $"duelo-g{game}.txt"), plainText, Encoding.UTF8);
        }
    }

    public IReadOnlyList<LogLine> GetSince(long sinceId)
    {
        lock (_sync)
        {
            return _lines.Where(line => line.Id > sinceId).ToArray();
        }
    }
}

public sealed record LogLine(long Id, DateTimeOffset Timestamp, string Text, string Kind);

/// <summary>
/// Copies everything written to the console into the session log, line by line,
/// so the wrapper's own logs ([Bot 0] ...) end up there too.
/// </summary>
public sealed class ConsoleTee : TextWriter
{
    private readonly TextWriter _inner;
    private readonly SessionLog _log;
    private readonly StringBuilder _buffer = new();
    private readonly object _sync = new();

    public ConsoleTee(TextWriter inner, SessionLog log)
    {
        _inner = inner;
        _log = log;
    }

    public override Encoding Encoding => _inner.Encoding;

    public override void Write(char value)
    {
        _inner.Write(value);
        lock (_sync)
        {
            if (value == '\n')
                Flush(emit: true);
            else if (value != '\r')
                _buffer.Append(value);
        }
    }

    public override void Write(string? value)
    {
        if (value == null)
            return;
        foreach (char c in value)
            Write(c);
    }

    public override void WriteLine(string? value)
    {
        Write(value);
        Write('\n');
    }

    private void Flush(bool emit)
    {
        string line = _buffer.ToString();
        _buffer.Clear();
        if (!emit || string.IsNullOrWhiteSpace(line))
            return;

        // Wrapper internals ([Bot 0] [SelectPlace] ...) vs. brain decisions ([Blackwings] ...).
        string kind = line.Contains("[ERROR]") ? "error"
            : line.StartsWith("[Bot ") ? "motor"
            : line.StartsWith("[") ? "decision"
            : "motor";
        _log.Add(line, kind);
    }
}
