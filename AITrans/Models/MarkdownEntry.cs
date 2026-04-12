using CommunityToolkit.Mvvm.ComponentModel;

namespace AITrans.Models;

public partial class MarkdownEntry : ObservableObject
{
    // Zero-width space appended to display text to work around Avalonia TextWrapping crash:
    // "Cannot split: requested length N consumes entire run."
    private const string Zwsp = "\u200B";

    public int Index { get; set; }

    private string _originalText = "";
    public string OriginalText
    {
        get => _originalText;
        set
        {
            if (SetProperty(ref _originalText, value))
                OnPropertyChanged(nameof(DisplayOriginalText));
        }
    }

    [ObservableProperty]
    private string _translatedText = "";

    /// <summary>Safe-for-display version of OriginalText (appends zero-width space).</summary>
    public string DisplayOriginalText => string.IsNullOrEmpty(OriginalText) ? "" : OriginalText + Zwsp;

    /// <summary>Safe-for-display version of TranslatedText (appends zero-width space).</summary>
    public string DisplayTranslatedText => string.IsNullOrEmpty(TranslatedText) ? "" : TranslatedText + Zwsp;

    partial void OnTranslatedTextChanged(string value)
    {
        OnPropertyChanged(nameof(DisplayTranslatedText));
    }
}
