using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace CrossRagfair.Hub;

internal sealed class HubConsoleFormatter() : ConsoleFormatter(FormatterName)
{
    public const string FormatterName = "crossragfair";

    public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider,
        TextWriter textWriter)
    {
        var message = logEntry.Formatter?.Invoke(logEntry.State, logEntry.Exception);
        if (string.IsNullOrEmpty(message) && logEntry.Exception is null) return;

        textWriter.Write(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        textWriter.Write(" [线程:");
        textWriter.Write(Environment.CurrentManagedThreadId);
        textWriter.Write("] [");
        textWriter.Write(LevelName(logEntry.LogLevel));
        textWriter.Write("] ");
        if (!string.IsNullOrEmpty(message)) textWriter.Write(message);
        if (logEntry.Exception is not null)
        {
            if (!string.IsNullOrEmpty(message)) textWriter.Write(' ');
            textWriter.Write(logEntry.Exception);
        }
        textWriter.WriteLine();
    }

    private static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "FATAL",
        _ => "NONE"
    };
}
