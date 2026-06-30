using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AITrans.Models;
using AITrans.Services;

namespace AITrans.ViewModels;

/// <summary>
/// Vocabulary "memory" / matching game built from the existing flashcard collection:
/// each card with both Side 1 (foreign word) and Side 2 (translation) filled in
/// contributes a pair of tiles that the user must find by flipping two at a time.
/// </summary>
public partial class MemoryGameViewModel : ViewModelBase
{
    private readonly FlashcardService _flashcardService;
    private static readonly Random _rng = new();

    private static readonly string[] PairColors =
    [
        "#E74C3C", "#2ECC71", "#F1C40F", "#9B59B6", "#1ABC9C", "#E67E22",
        "#FF6B9D", "#16A085", "#D35400", "#8E44AD", "#27AE60", "#2980B9",
        "#C0392B", "#F39C12", "#3F51B5", "#795548", "#00BCD4", "#FF5722",
        "#4CAF50", "#673AB7"
    ];

    private List<FlashCard> _sourceCards = [];
    private MemoryTile? _firstSelection;

    [ObservableProperty]
    private ObservableCollection<MemoryTile> _tiles = [];

    [ObservableProperty]
    private int _selectedPairCount = 8;

    public int[] PairCountOptions { get; } = [4, 6, 8, 10, 12, 16, 20];

    [ObservableProperty]
    private DifficultyOption _selectedDifficulty = DifficultyOption.Options[1]; // Unlearned

    public DifficultyOption[] DifficultyOptions { get; } = DifficultyOption.Options;

    [ObservableProperty]
    private int _pairsTotal;

    [ObservableProperty]
    private int _pairsFound;

    [ObservableProperty]
    private int _moves;

    [ObservableProperty]
    private bool _isBoardLocked;

    [ObservableProperty]
    private string _statusText = "";

    public bool HasTiles => Tiles.Count > 0;
    public bool IsGameComplete => PairsTotal > 0 && PairsFound == PairsTotal;

    public MemoryGameViewModel(FlashcardService flashcardService)
    {
        _flashcardService = flashcardService;
        _ = LoadAndStartAsync();
    }

    private async Task LoadAndStartAsync()
    {
        var cards = await _flashcardService.GetAllCardsAsync();
        _sourceCards = cards
            .Where(c => !string.IsNullOrWhiteSpace(c.FrontText) && !string.IsNullOrWhiteSpace(c.BackText))
            .ToList();

        StartNewGame();
    }

    /// <summary>Re-reads the word collection (e.g. after new flashcards were added) and starts a new game.</summary>
    [RelayCommand]
    private async Task Reload() => await LoadAndStartAsync();

    [RelayCommand]
    private void StartNewGame()
    {
        var filtered = DifficultyFilter.Filter(_sourceCards, SelectedDifficulty.Level);

        if (filtered.Count == 0)
        {
            Tiles = [];
            PairsTotal = 0;
            PairsFound = 0;
            Moves = 0;
            _firstSelection = null;
            IsBoardLocked = false;
            StatusText = SelectedDifficulty.Level switch
            {
                DifficultyLevel.NewAndHard => "Няма нови или трудни карти. Опитайте друго ниво.",
                DifficultyLevel.LearnedOnly => "Няма научени карти. Първо упражнявайте с флашкартите.",
                _ => "Няма карти с попълнени Страна 1 и Страна 2. Добавете думи в панела с флашкарти."
            };
            OnPropertyChanged(nameof(HasTiles));
            OnPropertyChanged(nameof(IsGameComplete));
            return;
        }

        int count = Math.Min(SelectedPairCount, filtered.Count);
        var chosen = PrioritizeByDifficulty(filtered, SelectedDifficulty.Level, count);

        var tiles = new List<MemoryTile>(count * 2);
        for (int i = 0; i < chosen.Count; i++)
        {
            var card = chosen[i];
            var pairBrush = new SolidColorBrush(Color.Parse(PairColors[i % PairColors.Length]));
            tiles.Add(new MemoryTile { PairId = card.Id, Text = card.FrontText, PairBrush = pairBrush });
            tiles.Add(new MemoryTile { PairId = card.Id, Text = card.BackText, PairBrush = pairBrush });
        }

        Tiles = new ObservableCollection<MemoryTile>(tiles.OrderBy(_ => _rng.Next()));
        PairsTotal = chosen.Count;
        PairsFound = 0;
        Moves = 0;
        _firstSelection = null;
        IsBoardLocked = false;
        StatusText = $"Намери двойките! ({PairsTotal} двойки)";

        OnPropertyChanged(nameof(HasTiles));
        OnPropertyChanged(nameof(IsGameComplete));
    }

    partial void OnSelectedPairCountChanged(int value) => StartNewGame();
    partial void OnSelectedDifficultyChanged(DifficultyOption value) => StartNewGame();

    [RelayCommand]
    private async Task FlipTile(MemoryTile tile)
    {
        if (IsBoardLocked || tile.IsFlipped || tile.IsMatched) return;

        tile.IsFlipped = true;

        if (_firstSelection == null)
        {
            _firstSelection = tile;
            return;
        }

        var first = _firstSelection;
        _firstSelection = null;
        Moves++;

        if (first.PairId == tile.PairId)
        {
            first.IsMatched = true;
            tile.IsMatched = true;
            PairsFound++;

            StatusText = IsGameComplete
                ? $"🎉 Браво! Намери всички {PairsTotal} двойки за {Moves} опита."
                : $"Намерени двойки: {PairsFound} / {PairsTotal}";

            OnPropertyChanged(nameof(IsGameComplete));
        }
        else
        {
            IsBoardLocked = true;
            await Task.Delay(800);
            first.IsFlipped = false;
            tile.IsFlipped = false;
            IsBoardLocked = false;
        }
    }

    private static List<FlashCard> PrioritizeByDifficulty(List<FlashCard> cards, DifficultyLevel level, int count)
    {
        if (level == DifficultyLevel.All)
            return cards.OrderBy(_ => _rng.Next()).Take(count).ToList();

        return cards
            .OrderByDescending(c => c.DifficultyScore)
            .ThenBy(c => c.Rating)
            .ThenBy(_ => _rng.Next())
            .Take(count)
            .ToList();
    }
}
