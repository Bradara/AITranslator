using CommunityToolkit.Mvvm.ComponentModel;

namespace AITrans.Models;

public partial class SrtEntry : ObservableObject
{
    private int _index;
    public int Index
    {
        get => _index;
        set => SetProperty(ref _index, value);
    }
    public string StartTime { get; set; } = "";
    public string EndTime { get; set; } = "";
    public string OriginalText { get; set; } = "";

    [ObservableProperty]
    private string _translatedText = "";

    public string TimeCode => $"{StartTime} --> {EndTime}";
}
