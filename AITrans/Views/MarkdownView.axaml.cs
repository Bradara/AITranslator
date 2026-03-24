using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using AITrans.Models;
using AITrans.ViewModels;

namespace AITrans.Views;

public partial class MarkdownView : UserControl
{
    public MarkdownView()
    {
        InitializeComponent();
        ParagraphGrid.SelectionChanged += OnGridSelectionChanged;
    }

    private void OnGridSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is MarkdownViewModel vm)
        {
            var indices = ParagraphGrid.SelectedItems
                .OfType<MarkdownEntry>()
                .Select(entry => entry.Index - 1) // Index is 1-based, collection is 0-based
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
