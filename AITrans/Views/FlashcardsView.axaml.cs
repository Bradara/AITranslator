using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using AITrans.Models;
using AITrans.ViewModels;

namespace AITrans.Views;

public partial class FlashcardsView : UserControl
{
    public FlashcardsView()
    {
        InitializeComponent();
    }

    private FlashcardsViewModel? Vm => DataContext as FlashcardsViewModel;

    // ── Open "All cards" modal window ─────────────────────────────────────────

    private async void OnShowAllCardsClick(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        var window = new FlashCardEditWindow(Vm.FlashcardService, Vm.Cards);
        if (TopLevel.GetTopLevel(this) is Window parent)
            await window.ShowDialog(parent);
        else
            window.Show();
        // Rebuild the quiz deck in case cards were deleted
        Vm.RebuildAfterEdit();
    }

    // ── CSV import (file dialog) ──────────────────────────────────────────────

    private async void OnImportCsvClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title       = "Избери CSV файл за импорт",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("CSV файлове") { Patterns = ["*.csv"] },
                new FilePickerFileType("Всички файлове") { Patterns = ["*"] }
            ]
        });

        if (files.Count > 0 && Vm != null)
            await Vm.ImportCsvCommand.ExecuteAsync(files[0].Path.LocalPath);
    }

    // ── Submit answer on Enter key in the answer TextBox ────────────────────

    private async void OnAnswerBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Vm != null)
            await Vm.SubmitAnswerCommand.ExecuteAsync(null);
    }
}
