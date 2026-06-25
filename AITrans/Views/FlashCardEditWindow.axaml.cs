using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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

    // Current search term and sort state
    private string _search = "";
    private string _sortProperty = "";
    private bool   _sortAscending = true;

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
        ApplyView();
    }

    // ── Search ────────────────────────────────────────────────────────────────

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        _search = SearchBox.Text ?? "";
        ApplyView();
    }

    private void OnClearSearchClick(object? sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";   // triggers OnSearchTextChanged
    }

    // ── Sorting ───────────────────────────────────────────────────────────────

    private void OnColumnSorting(object? sender, DataGridColumnEventArgs e)
    {
        var prop = e.Column.SortMemberPath;
        if (string.IsNullOrEmpty(prop)) return;

        if (_sortProperty == prop)
            _sortAscending = !_sortAscending;
        else
        {
            _sortProperty  = prop;
            _sortAscending = true;
        }

        ApplyView();
        e.Handled = true; // prevent default sort (we manage ItemsSource ourselves)
    }

    // ── Filter + sort → rebind ────────────────────────────────────────────────

    private void ApplyView()
    {
        IEnumerable<FlashCard> view = _cards;

        // Filter
        if (!string.IsNullOrWhiteSpace(_search))
        {
            var term = _search.Trim();
            view = view.Where(c =>
                c.FrontText.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                c.BackText .Contains(term, StringComparison.OrdinalIgnoreCase) ||
                c.UsageText.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        // Sort
        view = _sortProperty switch
        {
            nameof(FlashCard.FrontText)    => _sortAscending ? view.OrderBy(c => c.FrontText)    : view.OrderByDescending(c => c.FrontText),
            nameof(FlashCard.BackText)     => _sortAscending ? view.OrderBy(c => c.BackText)     : view.OrderByDescending(c => c.BackText),
            nameof(FlashCard.UsageText)    => _sortAscending ? view.OrderBy(c => c.UsageText)    : view.OrderByDescending(c => c.UsageText),
            nameof(FlashCard.CorrectCount) => _sortAscending ? view.OrderBy(c => c.CorrectCount) : view.OrderByDescending(c => c.CorrectCount),
            nameof(FlashCard.WrongCount)   => _sortAscending ? view.OrderBy(c => c.WrongCount)   : view.OrderByDescending(c => c.WrongCount),
            nameof(FlashCard.Rating)       => _sortAscending ? view.OrderBy(c => c.Rating)       : view.OrderByDescending(c => c.Rating),
            _ => view
        };

        CardsGrid.ItemsSource = view.ToList();
    }

    // ── Cell key handling ─────────────────────────────────────────────────────

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
            e.Handled     = true;
        }
    }

    // ── Cell edit committed ───────────────────────────────────────────────────

    private async void OnCellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit) return;
        if (e.Row.DataContext is not FlashCard card) return;

        await Task.Yield();
        await _service.UpdateCardAsync(card);

        // Refresh view to reflect any text changes in the filter
        ApplyView();
    }

    // ── Delete button ─────────────────────────────────────────────────────────

    private async void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.DataContext is not FlashCard card) return;

        await _service.DeleteCardAsync(card.Id);
        _cards.Remove(card);
        ApplyView();
    }

    // ── Close ─────────────────────────────────────────────────────────────────

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
