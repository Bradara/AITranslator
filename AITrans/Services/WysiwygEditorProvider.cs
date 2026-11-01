using System;
using System.Threading.Tasks;
using Avalonia.Controls;

namespace AITrans.Services;

/// <summary>Implemented by the Desktop-only WYSIWYG markdown editor control (a WebView host).
/// Kept in the shared project so the view/view-model can reference it without pulling in any
/// WebView package — only <see cref="AITrans.Desktop"/> ever implements it.</summary>
public interface IWysiwygEditorControl
{
    /// <summary>Replaces the editor's content with <paramref name="markdown"/> (does not raise
    /// <see cref="MarkdownChanged"/>), then scrolls the content area to <paramref name="scrollY"/>
    /// (its internal scrollTop, in CSS px) once loaded — 0 for the top of the page.</summary>
    void SetMarkdown(string markdown, double scrollY = 0);

    /// <summary>Returns the text currently selected in the editor, or null/empty if nothing is selected.</summary>
    Task<string?> GetSelectedTextAsync();

    /// <summary>Scrolls to and highlights the first on-page match for <paramref name="query"/>,
    /// using the browser's native in-page find. Best-effort — the caller is responsible for first
    /// switching to the page that actually contains the match (see
    /// <see cref="AITrans.ViewModels.MarkdownPreviewViewModel.GetPageIndexForCharIndex"/>).</summary>
    Task FindTextAsync(string query);

    /// <summary>Restyles the editor's chrome/content to match the host app's current theme and
    /// preview font size. Colors are "#RRGGBB" hex strings; <paramref name="fontSizePx"/> is the
    /// same raw number Preview mode's A+/A- buttons use (see MarkdownPreviewViewModel.PreviewFontSize),
    /// applied 1:1 as CSS px so both views scale together.</summary>
    void ApplyHostStyle(string backgroundHex, string textHex, string codeBackgroundHex, string borderHex, double fontSizePx);

    /// <summary>Raised (debounced) whenever the user edits the document, with the current markdown.</summary>
    event Action<string>? MarkdownChanged;

    /// <summary>Raised (debounced) whenever the user scrolls the content area, with its current
    /// scrollTop in CSS px — lets the host remember where the user was reading/editing.</summary>
    event Action<double>? ScrollChanged;

    /// <summary>Raised with a short, user-facing description when the editor fails — the web
    /// engine didn't start, the page didn't load, or its script threw.</summary>
    event Action<string>? ErrorOccurred;

    /// <summary>Raised on right-click inside the editor (whose own browser menu is suppressed)
    /// with the selected text at that moment and the click position in DIPs relative to the
    /// control, so the host can show its own context menu there.</summary>
    event Action<string, double, double>? ContextMenuRequested;

    /// <summary>Replaces the current selection with <paramref name="text"/> (or inserts it at the
    /// caret); an empty string deletes the selection.</summary>
    void InsertText(string text);
}

/// <summary>Registry the Desktop head populates at startup so the shared Preview view can create
/// a WYSIWYG editor control without referencing any WebView package. Android/iOS/Browser never
/// set <see cref="Factory"/>, so <see cref="IsAvailable"/> stays false and the WYSIWYG button
/// hides itself.</summary>
public static class WysiwygEditorProvider
{
    /// <summary>Creates a new editor host control, which must also implement <see cref="IWysiwygEditorControl"/>.</summary>
    public static Func<Control>? Factory { get; set; }

    public static bool IsAvailable => Factory is not null;
}
