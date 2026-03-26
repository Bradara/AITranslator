using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using AITrans.ViewModels;

namespace AITrans.Views;

public partial class MarkdownPreviewView : UserControl
{
    public MarkdownPreviewView()
    {
        InitializeComponent();
        // Track caret/selection changes via pointer and keyboard events
        PlainTextBox.PointerReleased += OnPlainTextPointerReleased;
        PlainTextBox.KeyUp += OnPlainTextKeyUp;
    }

    private void UpdateSelectionInVm()
    {
        if (DataContext is MarkdownPreviewViewModel vm)
            vm.SetSelectionStart(PlainTextBox.SelectionStart);
    }

    private void OnPlainTextPointerReleased(object? sender, PointerReleasedEventArgs e) => UpdateSelectionInVm();
    private void OnPlainTextKeyUp(object? sender, KeyEventArgs e) => UpdateSelectionInVm();

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
