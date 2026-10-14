namespace Shent.Core.Node;

public enum LogLevel
{
    Info,
    Success,
    Warning,
    Error,
}

public sealed record LogEntry(DateTimeOffset Time, LogLevel Level, string Category, string Message);

/// <summary>Registro en memoria (ring buffer). Nunca debe recibir claves, contraseñas ni semillas.</summary>
public sealed class NodeLog
{
    private const int Capacity = 500;
    private readonly LinkedList<LogEntry> _entries = new();

    public event Action<LogEntry>? Written;

    public void Write(LogLevel level, string category, string message)
    {
        var entry = new LogEntry(DateTimeOffset.Now, level, category, message);
        lock (_entries)
        {
            _entries.AddLast(entry);
            while (_entries.Count > Capacity) _entries.RemoveFirst();
        }
        Written?.Invoke(entry);
    }

    public void Info(string category, string message) => Write(LogLevel.Info, category, message);
    public void Success(string category, string message) => Write(LogLevel.Success, category, message);
    public void Warn(string category, string message) => Write(LogLevel.Warning, category, message);
    public void Error(string category, string message) => Write(LogLevel.Error, category, message);

    public List<LogEntry> Snapshot()
    {
        lock (_entries) return _entries.ToList();
    }
}
