using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace MistyStep.App.Logging;

/// <summary>
/// A small rolling file logger: <c>mistystep-YYYYMMDD.log</c>, 5 MB per file (then -1, -2, ...),
/// files older than 7 days deleted at startup. Exceptions are written as type and stack trace only,
/// never their message, so no content can slip into the log through an exception.
/// </summary>
public sealed class RollingFileLoggerProvider : ILoggerProvider
{
    public const long MaxFileBytes = 5 * 1024 * 1024;
    public const int KeepDays = 7;

    private readonly string _directory;
    private readonly Lock _gate = new();
    private StreamWriter? _writer;
    private string? _currentDay;
    private int _part;
    private bool _disposed;

    public RollingFileLoggerProvider(string directory)
    {
        _directory = directory;
        try
        {
            Directory.CreateDirectory(directory);
            DeleteOldFiles();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging must never stop the app.
        }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _writer?.Dispose();
            _writer = null;
        }
    }

    private void Write(LogLevel level, string category, string message, Exception? exception)
    {
        var now = DateTime.Now;
        var line = new StringBuilder()
            .Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
            .Append(" [").Append(Short(level)).Append("] ")
            .Append(category).Append(": ").Append(message);
        if (exception is not null)
            line.Append(" | ").Append(exception.GetType().FullName).Append(Environment.NewLine).Append(exception.StackTrace);

        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                var writer = EnsureWriter(now);
                writer.WriteLine(line.ToString());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _writer?.Dispose();
                _writer = null;
            }
        }
    }

    private StreamWriter EnsureWriter(DateTime now)
    {
        var day = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        if (_writer is not null && day == _currentDay && _writer.BaseStream.Length < MaxFileBytes) return _writer;

        _writer?.Dispose();
        if (day != _currentDay)
        {
            _currentDay = day;
            _part = 0;
        }

        while (true)
        {
            var path = Path.Combine(_directory, _part == 0 ? $"mistystep-{day}.log" : $"mistystep-{day}-{_part}.log");
            if (!File.Exists(path) || new FileInfo(path).Length < MaxFileBytes)
            {
                var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                return _writer;
            }

            _part++;
        }
    }

    private void DeleteOldFiles()
    {
        var cutoff = DateTime.Now.AddDays(-KeepDays);
        foreach (var file in Directory.EnumerateFiles(_directory, "mistystep-*.log"))
        {
            if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
        }
    }

    private static string Short(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???",
    };

    private sealed class FileLogger(RollingFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null; // scopes are never written

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            provider.Write(logLevel, category, formatter(state, null), exception);
        }
    }
}
