using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AITrans.Models;
using AITrans.ViewModels;

namespace AITrans.Views;

public partial class MarkdownView : UserControl
{
    private double _savedScrollY;
    private int _pendingScrollRow = -1;
    private MarkdownViewModel? _subscribedVm;

    public MarkdownView()
    {
        InitializeComponent();
        ParagraphGrid.SelectionChanged += OnGridSelectionChanged;
    }

    // ── Scroll position: save on tab deactivation, restore on activation ────

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != IsVisibleProperty) return;

        if (!change.GetNewValue<bool>())
            SaveScrollOffset();
        else
            Dispatcher.UIThread.Post(RestoreOrScrollToPending, DispatcherPriority.Loaded);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_subscribedVm != null)
        {
            _subscribedVm.PropertyChanged -= OnVmPropertyChanged;
            _subscribedVm = null;
        }
        if (DataContext is MarkdownViewModel vm)
        {
            _subscribedVm = vm;
            vm.PropertyChanged += OnVmPropertyChanged;
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MarkdownViewModel.ScrollToRow)) return;
        if (sender is not MarkdownViewModel vm || vm.ScrollToRow < 0) return;

        _pendingScrollRow = vm.ScrollToRow;
        vm.ScrollToRow = -1; // consume the signal

        if (IsVisible)
            Dispatcher.UIThread.Post(RestoreOrScrollToPending, DispatcherPriority.Loaded);
    }

    private ScrollViewer? GridScrollViewer()
        => ParagraphGrid.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

    private void SaveScrollOffset()
    {
        var sv = GridScrollViewer();
        if (sv != null) _savedScrollY = sv.Offset.Y;
    }

    private void RestoreOrScrollToPending()
    {
        if (_pendingScrollRow >= 0)
        {
            ScrollGridToRow(_pendingScrollRow);
            _pendingScrollRow = -1;
        }
        else
        {
            var sv = GridScrollViewer();
            if (sv != null) sv.Offset = new Vector(0, _savedScrollY);
        }
    }

    private void ScrollGridToRow(int rowIndex)
    {
        if (DataContext is not MarkdownViewModel vm || vm.Paragraphs.Count == 0) return;
        rowIndex = Math.Clamp(rowIndex, 0, vm.Paragraphs.Count - 1);
        ParagraphGrid.ScrollIntoView(vm.Paragraphs[rowIndex], null);
    }

    // ── Existing handlers ────────────────────────────────────────────────────

    private void OnGridSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MarkdownViewModel vm)
        {
            var indices = ParagraphGrid.SelectedItems
                .OfType<MarkdownEntry>()
                .Select(entry => entry.Index - 1)
                .Where(i => i >= 0)
                .OrderBy(i => i)
                .ToList();
            vm.SetSelectedIndices(indices);
        }
    }

    private async void OnCopyResultClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MarkdownViewModel vm)
        {
            var text = vm.GetCombinedTranslation();
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
            {
                await clipboard.SetTextAsync(text);
                vm.StatusText = "Translation copied to clipboard.";
            }
        }
    }

    private async void OnOpenFileClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Markdown File",
            AllowMultiple = false,
            FileTypeFilter = [
                new FilePickerFileType("Markdown") { Patterns = ["*.md", "*.markdown"] },
                new FilePickerFileType("All Files") { Patterns = ["*"] }
            ]
        });

        if (files.Count > 0 && DataContext is MarkdownViewModel vm)
            vm.LoadFile(files[0].Path.LocalPath);
    }

    private async void OnSaveFileClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        if (DataContext is not MarkdownViewModel vm) return;

        var suggestedName = vm.LoadedFilePath is not null
            ? System.IO.Path.GetFileNameWithoutExtension(vm.LoadedFilePath) + "_translated.md"
            : "translation.md";

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Translation",
            DefaultExtension = "md",
            SuggestedFileName = suggestedName,
            FileTypeChoices = [
                new FilePickerFileType("Markdown") { Patterns = ["*.md"] }
            ]
        });

        if (file != null)
            vm.SaveTranslation(file.Path.LocalPath);
    }
}

