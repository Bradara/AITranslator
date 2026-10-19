namespace AITrans.Models;

/// <summary>A single heading extracted from a markdown document, used to build a clickable
/// table of contents in the preview panel.</summary>
public class TocItem
{
    public int Level { get; }
    public string Title { get; }

    /// <summary>Index into the raw-paragraph list the preview uses for pagination — lets
    /// navigation jump straight to the page that contains this heading.</summary>
    public int ParagraphIndex { get; }

    public TocItem(int level, string title, int paragraphIndex)
    {
        Level = level;
        Title = title;
        ParagraphIndex = paragraphIndex;
    }

    /// <summary>Indented and bulleted for sub-headings, so the list reads as a nested
    /// outline without needing a value converter in the view.</summary>
    public string DisplayTitle => Level <= 1 ? Title : new string(' ', (Level - 1) * 3) + "• " + Title;

    /// <summary>Slightly smaller type for deeper heading levels.</summary>
    public double FontSize => Level switch { 1 => 13.5, 2 => 12.5, _ => 11.5 };
}
