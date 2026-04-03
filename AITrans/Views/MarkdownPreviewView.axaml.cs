using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.Input;
using AITrans.ViewModels;

namespace AITrans.Views;

public partial class MarkdownPreviewView : UserControl
{
    // Tracking styles we inject so we can remove them before updating
    private readonly List<Style> _fontSizeStyles = [];

    public MarkdownPreviewView()
    {
        InitializeComponent();
        // Track caret position in the raw editor for TTS "Read from Selection"
        RawEditor.PointerReleased += OnRawEditorPointerReleased;
        RawEditor.KeyUp += OnRawEditorKeyUp;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (DataContext is not MarkdownPreviewViewModel vm) return;

        vm.PropertyChanged += (s, pe) =>
        {
            // Update AssetPathRoot whenever the loaded file changes so relative images resolve correctly
            if (pe.PropertyName == nameof(MarkdownPreviewViewModel.LoadedFilePath)
                && !string.IsNullOrEmpty(vm.LoadedFilePath))
            {
                var dir = Path.GetDirectoryName(vm.LoadedFilePath);
                if (dir != null) MarkViewer.AssetPathRoot = dir;
            }

            // Rebuild font-size override styles when the user clicks A+/A-
            if (pe.PropertyName == nameof(MarkdownPreviewViewModel.PreviewFontSize))
                ApplyFontSizeStyles(vm.PreviewFontSize);
        };

        // Intercept hyperlink clicks — route .md links to in-app navigation, web links to the browser
        if (MarkViewer.Engine is global::Markdown.Avalonia.Markdown mdEngine)
        {
            mdEngine.HyperlinkCommand = new RelayCommand<string>(url =>
            {
                if (!string.IsNullOrEmpty(url))
                    vm.NavigateTo(url);
            });
        }

        // Apply the initial font size
        ApplyFontSizeStyles(vm.PreviewFontSize);
    }

    // Inject Avalonia style objects AFTER the MarkdownStyle so they win the cascade.
    // CTextBlock shares TextBlock.FontSizeProperty (via AddOwner), so setting
    // TextBlock.FontSize through a class-based Avalonia style correctly resizes the text.
    private void ApplyFontSizeStyles(double baseSize)
    {
        // Remove old overrides first
        foreach (var s in _fontSizeStyles)
            MarkViewer.Styles.Remove(s);
        _fontSizeStyles.Clear();

        void Add(string cls, double size)
        {
            var style = new Style(sel => sel.Class(cls))
            {
                Setters = { new Setter(TextBlock.FontSizeProperty, size) }
            };
            _fontSizeStyles.Add(style);
            MarkViewer.Styles.Add(style);
        }

        // Body text
        Add("Paragraph",   baseSize);
        Add("Blockquote",  baseSize);
        Add("Note",        baseSize);
        Add("ListMarker",  baseSize);

        // Table cells
        Add("TableHeader", baseSize);
        Add("TableFooter", baseSize);

        // Headings — standard proportional ratios
        Add("Heading1",    Math.Round(baseSize * 2.00));
        Add("Heading2",    Math.Round(baseSize * 1.50));
        Add("Heading3",    Math.Round(baseSize * 1.25));
        Add("Heading4",    Math.Round(baseSize * 1.10));
        Add("Heading5",    baseSize);
        Add("Heading6",    Math.Round(baseSize * 0.90));
    }

    private void UpdateSelectionInVm()
    {
        if (DataContext is MarkdownPreviewViewModel vm)
            vm.SetSelectionStart(RawEditor.SelectionStart);
    }

    private void OnRawEditorPointerReleased(object? sender, PointerReleasedEventArgs e) => UpdateSelectionInVm();
    private void OnRawEditorKeyUp(object? sender, KeyEventArgs e) => UpdateSelectionInVm();
    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MarkdownPreviewViewModel vm) return;

        if (!string.IsNullOrEmpty(vm.LoadedFilePath))
        {
            vm.SaveToFile(vm.LoadedFilePath);
            return;
        }

        // No file loaded yet — show Save As picker
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Markdown File",
            SuggestedFileName = "document.md",
            FileTypeChoices =
            [
                new FilePickerFileType("Markdown") { Patterns = ["*.md", "*.markdown"] },
                new FilePickerFileType("Text") { Patterns = ["*.txt"] }
            ]
        });

        if (file != null)
            vm.SaveToFile(file.Path.LocalPath);
    }
    private async void OnOpenFileClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Markdown File",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Markdown") { Patterns = ["*.md", "*.markdown", "*.txt"] },
                new FilePickerFileType("All Files") { Patterns = ["*"] }
            ]
        });

        if (files.Count > 0 && DataContext is MarkdownPreviewViewModel vm)
            vm.LoadFile(files[0].Path.LocalPath);
    }
}
