using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AITrans.Models;

/// <summary>User-assigned difficulty rating for a flashcard.</summary>
public enum CardRating
{
    New     = 0,   // never rated – always shown first
    Hard    = 1,   // shown with high priority
    Normal  = 2,   // shown with medium priority
    Easy    = 3,   // shown with low priority
    Learned = 4    // excluded from the quiz deck
}

public partial class FlashCard : ObservableObject
{
    public int Id { get; set; }

    [ObservableProperty]
    private string _frontText = "";   // Side 1 – foreign word / phrase

    [ObservableProperty]
    private string _backText = "";    // Side 2 – translation (Bulgarian)

    [ObservableProperty]
    private string _usageText = "";   // Side 3 – usage examples / thesaurus

    [ObservableProperty]
    private int _correctCount;

    [ObservableProperty]
    private int _wrongCount;

    [ObservableProperty]
    private CardRating _rating = CardRating.New;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// 0.0 = never tested / all correct;  1.0 = all wrong.
    /// Used as a secondary sort key within each rating group.
    /// </summary>
    public double DifficultyScore =>
        (CorrectCount + WrongCount) == 0
            ? 0.0
            : (double)WrongCount / (CorrectCount + WrongCount);
}
