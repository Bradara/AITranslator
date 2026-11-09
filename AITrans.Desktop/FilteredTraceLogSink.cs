using System;
using Avalonia;
using Avalonia.Logging;

namespace AITrans.Desktop;

/// <summary>Wraps Avalonia's standard trace sink and drops known-harmless noise. Currently:
/// "PlatformImpl is null, couldn't handle input." — logged when input (typically keystrokes
/// while the WebView2-based WYSIWYG editor has focus) reaches an already-closed window
/// (dialogs, context-menu popups). Avalonia just discards that input; nothing is wrong.</summary>
internal sealed class FilteredTraceLogSink(ILogSink inner) : ILogSink
{
    private static readonly string[] SuppressedTemplates =
    [
        "PlatformImpl is null, couldn't handle input.",
    ];

    private static bool IsSuppressed(string messageTemplate)
    {
        foreach (var t in SuppressedTemplates)
            if (messageTemplate.StartsWith(t, StringComparison.Ordinal))
                return true;
        return false;
    }

    public bool IsEnabled(LogEventLevel level, string area) => inner.IsEnabled(level, area);

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
    {
        if (!IsSuppressed(messageTemplate))
            inner.Log(level, area, source, messageTemplate);
    }

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate,
        params object?[] propertyValues)
    {
        if (!IsSuppressed(messageTemplate))
            inner.Log(level, area, source, messageTemplate, propertyValues);
    }
}

internal static class FilteredTraceLogSinkExtensions
{
    /// <summary>Same as <c>LogToTrace()</c>, but with <see cref="FilteredTraceLogSink"/> in front.</summary>
    public static AppBuilder LogToTraceFiltered(this AppBuilder builder,
        LogEventLevel level = LogEventLevel.Warning, params string[] areas)
    {
        // TraceLogSink is internal to Avalonia — let LogToTrace() install it, then wrap it.
        builder.LogToTrace(level, areas);
        if (Logger.Sink is { } sink and not FilteredTraceLogSink)
            Logger.Sink = new FilteredTraceLogSink(sink);
        return builder;
    }
}
