using System;
using System.Collections.Generic;
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

public partial class SubtitlesView : UserControl
{
    private double _savedScrollY;
    private int _pendingScrollRow = -1;
    private SubtitlesViewModel? _subscribedVm;

    public SubtitlesView()
    {
        InitializeComponent();
        SubtitleGrid.SelectionChanged += OnGridSelectionChanged;
    }

    // ── Scroll position: save on tab deactivation, restore on activation ────

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != IsVisibleProperty) return;

        if (!change.GetNewValue<bool>())
            SaveScrollOffset();
        else
        {
            if (DataContext is SubtitlesViewModel vm)
                vm.RequestRestoreScroll();
            Dispatcher.UIThread.Post(RestoreOrScrollToPending, DispatcherPriority.Loaded);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (DataContext is SubtitlesViewModel vm)
            vm.RequestRestoreScroll();
        Dispatcher.UIThread.Post(RestoreOrScrollToPending, DispatcherPriority.Loaded);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        SaveScrollOffset();
        if (DataContext is SubtitlesViewModel vm)
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
        if (DataContext is SubtitlesViewModel vm)
        {
            _subscribedVm = vm;
            vm.PropertyChanged += OnVmPropertyChanged;
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SubtitlesViewModel.ScrollToRow)) return;
        if (sender is not SubtitlesViewModel vm || vm.ScrollToRow < 0) return;

        _pendingScrollRow = vm.ScrollToRow;
        vm.ScrollToRow = -1; // consume the signal

        if (IsVisible)
            Dispatcher.UIThread.Post(RestoreOrScrollToPending, DispatcherPriority.Loaded);
    }

    private ScrollViewer? GridScrollViewer()
        => SubtitleGrid.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

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
        if (DataContext is not SubtitlesViewModel vm || vm.Entries.Count == 0) return;
        rowIndex = Math.Clamp(rowIndex, 0, vm.Entries.Count - 1);
        SubtitleGrid.ScrollIntoView(vm.Entries[rowIndex], null);
    }

    private void OnSubtitleGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(SubtitleGrid).Properties.IsRightButtonPressed) return;

        if (e.Source is not Control source) return;
        var row = source.GetVisualAncestors().OfType<DataGridRow>().FirstOrDefault();
        if (row == null || row.DataContext == null) return;

        if (!row.IsSelected)
            SubtitleGrid.SelectedItem = row.DataContext;
    }

    private void OnGridSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is SubtitlesViewModel vm)
        {
            var indices = SubtitleGrid.SelectedItems
                .OfType<SrtEntry>()
                .Select(entry => entry.Index - 1) // SRT index is 1-based, collection is 0-based
                .Where(i => i >= 0)
                .OrderBy(i => i)
                .ToList();
            vm.SetSelectedIndices(indices);
        }
    }

    private async void OnRestoreCacheClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SubtitlesViewModel vm) return;
        var window = new CacheHistoryWindow(vm.CacheService, isSubtitle: true);
        if (TopLevel.GetTopLevel(this) is Window parent)
            await window.ShowDialog(parent);
        else
            window.Show();
        vm.RefreshCacheInfo();
        if (window.SelectedKey != null)
            vm.LoadCacheFromKey(window.SelectedKey);
    }

    private async void OnOpenFileClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open SRT File",
            AllowMultiple = false,
            FileTypeFilter = [
                new FilePickerFileType("SRT Subtitles") { Patterns = ["*.srt"] }
            ]
        });

        if (files.Count > 0 && DataContext is SubtitlesViewModel vm2)
        {
            vm2.LoadFile(files[0].Path.LocalPath);
        }
    }

    private async void OnSaveFileClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Translated SRT",
            DefaultExtension = "srt",
            FileTypeChoices = [
                new FilePickerFileType("SRT Subtitles") { Patterns = ["*.srt"] }
            ]
        });

        if (file != null && DataContext is SubtitlesViewModel vm2)
        {
            vm2.SaveFile(file.Path.LocalPath);
        }
    }
}
