using CommunityToolkit.Mvvm.ComponentModel;

namespace AITrans.Models;

public partial class MarkdownEntry : ObservableObject
{
    // Zero-width space appended to display text to work around Avalonia TextWrapping crash:
    // "Cannot split: requested length N consumes entire run."
    private const string Zwsp = "\u200B";

    private int _index;
    public int Index
    {
        get => _index;
        set => SetProperty(ref _index, value);
    }

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

    [ObservableProperty]
    private bool _isSearchMatch;

    [ObservableProperty]
    private bool _isCurrentSearchMatch;

    /// <summary>Safe-for-display version of OriginalText (appends zero-width space).</summary>
    public string DisplayOriginalText => string.IsNullOrEmpty(OriginalText) ? "" : OriginalText + Zwsp;

    /// <summary>Safe-for-display version of TranslatedText (appends zero-width space).</summary>
    public string DisplayTranslatedText => string.IsNullOrEmpty(TranslatedText) ? "" : TranslatedText + Zwsp;

    /// <summary>Row background reflecting search-match state, bound by the DataGrid row style.</summary>
    public string RowHighlight => IsCurrentSearchMatch ? "#803DDC97" : IsSearchMatch ? "#40FFD54F" : "Transparent";

    partial void OnTranslatedTextChanged(string value)
    {
        OnPropertyChanged(nameof(DisplayTranslatedText));
    }

    partial void OnIsSearchMatchChanged(bool value) => OnPropertyChanged(nameof(RowHighlight));

    partial void OnIsCurrentSearchMatchChanged(bool value) => OnPropertyChanged(nameof(RowHighlight));
}
