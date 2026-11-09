namespace AITrans.Models;

/// <summary>A reader mark — <c>==text==</c>, optionally followed by an inline note
/// <c>^[note]</c> — extracted from a markdown document for the "Отбелязани" side list.</summary>
public class MarkItem
{
    /// <summary>The marked text with inline markdown stripped — what the reader sees.</summary>
    public string Text { get; }

    /// <summary>The exact source text, markers included (<c>==…==</c>), used to locate the
    /// mark in the raw editor.</summary>
    public string Source { get; }

    public string? Note { get; }

    /// <summary>Index into the raw-paragraph list the preview uses for pagination.</summary>
    public int ParagraphIndex { get; }

    /// <summary>How many identical <see cref="Source"/> strings precede this one in the
    /// document — tells apart repeated marks of the same text.</summary>
    public int Occurrence { get; }

    public MarkItem(string text, string source, string? note, int paragraphIndex, int occurrence)
    {
        Text = text;
        Source = source;
        Note = note;
        ParagraphIndex = paragraphIndex;
        Occurrence = occurrence;
    }

    public string DisplayText => Text.Length > 90 ? Text[..90] + "…" : Text;
    public bool HasNote => !string.IsNullOrWhiteSpace(Note);
}
