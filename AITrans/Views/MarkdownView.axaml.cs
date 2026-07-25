using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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
    private int _pendingScrollRow = -1;
    private MarkdownViewModel? _subscribedVm;
    private ScrollViewer? _subscribedScrollViewer;

    public MarkdownView()
    {
        InitializeComponent();
        ParagraphGrid.SelectionChanged += OnGridSelectionChanged;
    }

    private async void OnRestoreCacheClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MarkdownViewModel vm) return;
        var window = new CacheHistoryWindow(vm.CacheService, isSubtitle: false);
        if (TopLevel.GetTopLevel(this) is Window parent)
            await window.ShowDialog(parent);
        else
            window.Show();
        vm.RefreshCacheInfo();
        if (window.SelectedKey != null)
            vm.LoadCacheFromKey(window.SelectedKey);
    }

    // ── Active row: suggested (not auto-scrolled) on tab activation ─────────
    // Auto-scrolling the virtualized DataGrid right when the tab becomes visible proved
    // unreliable (rows not yet realized at that point). Instead we just pre-fill the
    // "go to row" navigator with the last active row; the user confirms via "Иди".

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != IsVisibleProperty) return;
        if (!change.GetNewValue<bool>()) return;

        RequestRowSuggestion();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        RequestRowSuggestion();
    }

    private void RequestRowSuggestion()
    {
        if (DataContext is MarkdownViewModel vm)
            vm.SuggestRestoreRow();
        Dispatcher.UIThread.Post(SubscribeScrollChanged, DispatcherPriority.Loaded);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_subscribedScrollViewer != null)
        {
            _subscribedScrollViewer.ScrollChanged -= OnGridScrollChanged;
            _subscribedScrollViewer = null;
        }
        if (DataContext is MarkdownViewModel vm)
            vm.PersistSessionState();
        base.OnDetachedFromVisualTree(e);
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

        Dispatcher.UIThread.Post(RestoreOrScrollToPending, DispatcherPriority.Loaded);
    }

    private ScrollViewer? GridScrollViewer()
        => ParagraphGrid.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

    private void RestoreOrScrollToPending()
    {
        SubscribeScrollChanged();
        if (_pendingScrollRow < 0) return;
        ScrollGridToRow(_pendingScrollRow);
        _pendingScrollRow = -1;
    }

    // ── Active-row tracking while scrolling (mouse wheel, scrollbar, keyboard) ─

    private void SubscribeScrollChanged()
    {
        var sv = GridScrollViewer();
        if (sv == null || sv == _subscribedScrollViewer) return;

        if (_subscribedScrollViewer != null)
            _subscribedScrollViewer.ScrollChanged -= OnGridScrollChanged;

        _subscribedScrollViewer = sv;
        sv.ScrollChanged += OnGridScrollChanged;
    }

    private void OnGridScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        // Defer until after layout — rows aren't re-arranged for the new offset yet at the
        // moment ScrollChanged fires, so reading Bounds/TranslatePoint here would be stale
        // and pick a row a few positions off from what's actually on screen.
        Dispatcher.UIThread.Post(UpdateActiveRowFromScroll, DispatcherPriority.Loaded);
    }

    private void UpdateActiveRowFromScroll()
    {
        if (DataContext is not MarkdownViewModel vm) return;
        if (GetTopmostVisibleRow()?.DataContext is MarkdownEntry entry)
            vm.NotifyActiveRow(entry.Index - 1);
    }

    /// <summary>Finds the row currently at (or nearest below) the top edge of the grid's viewport.</summary>
    private DataGridRow? GetTopmostVisibleRow()
    {
        DataGridRow? best = null;
        var bestY = double.MaxValue;

        foreach (var row in ParagraphGrid.GetVisualDescendants().OfType<DataGridRow>())
        {
            var pt = row.TranslatePoint(new Point(0, 0), ParagraphGrid);
            if (pt == null) continue;

            var y = pt.Value.Y;
            if (y < -row.Bounds.Height) continue; // scrolled fully above the viewport
            if (y < bestY)
            {
                bestY = y;
                best = row;
            }
        }

        return best;
    }

    private void ScrollGridToRow(int rowIndex)
    {
        if (DataContext is not MarkdownViewModel vm || vm.Paragraphs.Count == 0) return;
        rowIndex = Math.Clamp(rowIndex, 0, vm.Paragraphs.Count - 1);
        ParagraphGrid.ScrollIntoView(vm.Paragraphs[rowIndex], null);
    }

    // ── Existing handlers ────────────────────────────────────────────────────

    private void OnParagraphGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(ParagraphGrid).Properties.IsRightButtonPressed) return;

        if (e.Source is not Control source) return;
        var row = source.GetVisualAncestors().OfType<DataGridRow>().FirstOrDefault();
        if (row == null || row.DataContext == null) return;

        if (!row.IsSelected)
            ParagraphGrid.SelectedItem = row.DataContext;
    }

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

    private async void OnImportEbookClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        if (DataContext is not MarkdownViewModel vm) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import EPUB/FB2",
            AllowMultiple = false,
            FileTypeFilter = [
                new FilePickerFileType("Ebook") { Patterns = ["*.epub", "*.fb2"] },
                new FilePickerFileType("All Files") { Patterns = ["*"] }
            ]
        });

        if (files.Count == 0) return;

        var sourcePath = files[0].Path.LocalPath;
        var outputRoot = vm.EbookWorkingFolder;

        if (string.IsNullOrWhiteSpace(outputRoot) || !Directory.Exists(outputRoot))
        {
            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select working folder for ebook import",
                AllowMultiple = false
            });

            if (folders.Count == 0)
            {
                vm.StatusText = "Import canceled. Working folder not set.";
                return;
            }

            outputRoot = folders[0].Path.LocalPath;
            vm.UpdateEbookWorkingFolder(outputRoot);
        }

        await vm.ImportEbookAsync(sourcePath, outputRoot);
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

    private async void OnSaveOriginalClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        if (DataContext is not MarkdownViewModel vm) return;

        // If a file is already loaded, save directly without prompting
        if (vm.LoadedFilePath is not null)
        {
            vm.SaveOriginal(vm.LoadedFilePath);
            return;
        }

        await ShowSaveOriginalDialog(topLevel, vm);
    }

    private async void OnSaveOriginalAsClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;
        if (DataContext is not MarkdownViewModel vm) return;

        await ShowSaveOriginalDialog(topLevel, vm);
    }

    private async Task ShowSaveOriginalDialog(TopLevel topLevel, MarkdownViewModel vm)
    {
        var suggestedName = vm.LoadedFilePath is not null
            ? System.IO.Path.GetFileName(vm.LoadedFilePath)
            : "original.md";

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Original Text",
            DefaultExtension = "md",
            SuggestedFileName = suggestedName,
            FileTypeChoices = [
                new FilePickerFileType("Markdown") { Patterns = ["*.md"] },
                new FilePickerFileType("Text") { Patterns = ["*.txt"] },
                new FilePickerFileType("All Files") { Patterns = ["*"] }
            ]
        });

        if (file != null)
            vm.SaveOriginal(file.Path.LocalPath);
    }
}

