using CommunityToolkit.Mvvm.ComponentModel;

namespace AITrans.Models;

public partial class MarkdownEntry : ObservableObject
{
    public int Index { get; set; }
    public string OriginalText { get; set; } = "";

    [ObservableProperty]
    private string _translatedText = "";
}
