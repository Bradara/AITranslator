using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using AITrans.Models;
using AITrans.ViewModels;

namespace AITrans.Views;

public partial class SubtitlesView : UserControl
{
    public SubtitlesView()
    {
        InitializeComponent();
        SubtitleGrid.SelectionChanged += OnGridSelectionChanged;
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
