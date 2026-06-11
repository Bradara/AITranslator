using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AITrans.Models;

/// <summary>One tile on the memory/matching game board. Two tiles share the same PairId
/// (one shows the FlashCard's front text, the other its back/translation text).</summary>
public partial class MemoryTile : ObservableObject
{
    private static readonly IBrush HiddenBrush   = new SolidColorBrush(Color.Parse("#7F8C8D"));
    private static readonly IBrush RevealedBrush = new SolidColorBrush(Color.Parse("#3498DB"));

    public int PairId { get; init; }

    /// <summary>The word/translation revealed once the tile is flipped.</summary>
    public string Text { get; init; } = "";

    /// <summary>Background color unique to this tile's pair, shown once the pair is matched
    /// so the user can see at a glance which two tiles belong together.</summary>
    public IBrush PairBrush { get; init; } = HiddenBrush;

    [ObservableProperty]
    private bool _isFlipped;

    [ObservableProperty]
    private bool _isMatched;

    public string DisplayText => IsFlipped || IsMatched ? Text : "❓";

    public IBrush TileBrush => IsMatched ? PairBrush : IsFlipped ? RevealedBrush : HiddenBrush;

    partial void OnIsFlippedChanged(bool value)
    {
        OnPropertyChanged(nameof(DisplayText));
        OnPropertyChanged(nameof(TileBrush));
    }

    partial void OnIsMatchedChanged(bool value)
    {
        OnPropertyChanged(nameof(DisplayText));
        OnPropertyChanged(nameof(TileBrush));
    }
}
