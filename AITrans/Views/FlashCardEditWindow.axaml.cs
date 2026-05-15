using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using AITrans.Models;
using AITrans.Services;

namespace AITrans.Views;

/// <summary>
/// Modal window for viewing, editing and deleting all flash cards.
/// Pass the live <see cref="ObservableCollection{FlashCard}"/> so the main view
/// reflects changes immediately after the window closes.
/// </summary>
public partial class FlashCardEditWindow : Window
{
    /// <summary>Exposed as x:Static so the Rating ComboBox can use it as ItemsSource.</summary>
    public static readonly CardRating[] RatingValues = Enum.GetValues<CardRating>();

    private readonly FlashcardService _service;
    private readonly ObservableCollection<FlashCard> _cards;

    // Parameterless ctor required by Avalonia XAML compiler
    public FlashCardEditWindow()
    {
        InitializeComponent();
        _service = null!;
        _cards   = null!;
    }

    public FlashCardEditWindow(FlashcardService service, ObservableCollection<FlashCard> cards)
    {
        _service = service;
        _cards   = cards;
        InitializeComponent();
        CardsGrid.ItemsSource = _cards;
    }

    // ── Cell key handling ────────────────────────────────────────────────

    /// <summary>
    /// Ctrl+Enter inserts a newline at the caret.
    /// Plain Enter is NOT handled here — it bubbles up to the DataGrid which commits the edit.
    /// </summary>
    private void OnCellKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return && e.KeyModifiers.HasFlag(KeyModifiers.Control)
            && sender is TextBox tb)
        {
            var idx  = tb.CaretIndex;
            var text = tb.Text ?? "";
            tb.Text       = text.Insert(idx, "\n");
            tb.CaretIndex = idx + 1;
            e.Handled     = true; // prevent DataGrid from committing
        }
        // plain Enter: not handled — DataGrid commits and moves to next row
    }

    // ── Cell edit committed ───────────────────────────────────────────────────

    private async void OnCellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        // Only persist on commit (not cancel)
        if (e.EditAction != DataGridEditAction.Commit) return;
        if (e.Row.DataContext is not FlashCard card) return;

        // Avalonia fires CellEditEnding before the binding is written back,
        // so we need to post the save to the dispatcher to run after commit.
        await Task.Yield();
        await _service.UpdateCardAsync(card);
    }

    // ── Delete button ─────────────────────────────────────────────────────────

    private async void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.DataContext is not FlashCard card) return;

        await _service.DeleteCardAsync(card.Id);
        _cards.Remove(card);
    }

    // ── Close ─────────────────────────────────────────────────────────────────

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
