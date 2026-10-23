using Avalonia.Logging;
using Microsoft.Extensions.Logging;
using AvaloniaLevel = Avalonia.Logging.LogEventLevel;

namespace DownholeStudio.App.Services;

/// <summary>
/// Sends the UI framework's own warnings and errors (layout, binding, rendering) to the application log, so
/// a diagnostic package shows them. Information and below are dropped to keep the log small.
/// </summary>
public sealed class AvaloniaLogForwarder(ILogger logger) : ILogSink
{
    public bool IsEnabled(AvaloniaLevel level, string area) => level >= AvaloniaLevel.Warning;

    public void Log(AvaloniaLevel level, string area, object? source, string messageTemplate) =>
        Log(level, area, source, messageTemplate, []);

    public void Log(AvaloniaLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
    {
        if (!IsEnabled(level, area))
        {
            return;
        }

        var message = propertyValues.Length == 0 ? messageTemplate : $"{messageTemplate} | {string.Join(" | ", propertyValues)}";
        logger.Log(level >= AvaloniaLevel.Error ? LogLevel.Error : LogLevel.Warning, "[{Area}] {Source}: {Message}", area, source?.GetType().Name, message);
    }
}
