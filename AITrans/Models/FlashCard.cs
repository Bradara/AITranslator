using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AITrans.Models;

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

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// 0.0 = never tested / all correct;  1.0 = all wrong.
    /// Used to sort the quiz deck so hardest cards appear first.
    /// </summary>
    public double DifficultyScore =>
        (CorrectCount + WrongCount) == 0
            ? 0.0
            : (double)WrongCount / (CorrectCount + WrongCount);
}
