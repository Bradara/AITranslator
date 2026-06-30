using System.Collections.Generic;
using System.Linq;
using AITrans.Models;

namespace AITrans.ViewModels;

/// <summary>Word-selection level shared by the Flashcards quiz deck and the Memory game,
/// based on each card's user-assigned <see cref="CardRating"/>.</summary>
public enum DifficultyLevel
{
    NewAndHard,
    Unlearned,
    All,
    LearnedOnly
}

public record DifficultyOption(DifficultyLevel Level, string DisplayName)
{
    public static readonly DifficultyOption[] Options =
    [
        new(DifficultyLevel.NewAndHard,  "🔴 Нови и трудни"),
        new(DifficultyLevel.Unlearned,   "🟡 Незаучени"),
        new(DifficultyLevel.All,         "🔵 Всички"),
        new(DifficultyLevel.LearnedOnly, "🟢 Само научени"),
    ];

    public override string ToString() => DisplayName;
}

public static class DifficultyFilter
{
    public static List<FlashCard> Filter(IEnumerable<FlashCard> cards, DifficultyLevel level) =>
        level switch
        {
            DifficultyLevel.NewAndHard => cards.Where(c => c.Rating is CardRating.New or CardRating.Hard).ToList(),
            DifficultyLevel.Unlearned => cards.Where(c => c.Rating != CardRating.Learned).ToList(),
            DifficultyLevel.LearnedOnly => cards.Where(c => c.Rating == CardRating.Learned).ToList(),
            _ => cards.ToList()
        };
}
