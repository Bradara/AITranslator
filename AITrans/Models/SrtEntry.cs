using CommunityToolkit.Mvvm.ComponentModel;

namespace AITrans.Models;

public partial class SrtEntry : ObservableObject
{
    public int Index { get; set; }
    public string StartTime { get; set; } = "";
    public string EndTime { get; set; } = "";
    public string OriginalText { get; set; } = "";

    [ObservableProperty]
    private string _translatedText = "";

    public string TimeCode => $"{StartTime} --> {EndTime}";
}
