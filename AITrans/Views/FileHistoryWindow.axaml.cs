using System.Collections.ObjectModel;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using AITrans.Services;

namespace AITrans.Views;

public class FileHistoryItem
{
    public string FilePath { get; init; } = "";
    public string FileName => Path.GetFileName(FilePath);
    public string LastOpenedText { get; init; } = "";
}

/// <summary>
/// Dialog that shows all preview file history entries and lets the user open or delete them.
/// Read <see cref="SelectedFilePath"/> after ShowDialog returns — null means cancelled.
/// </summary>
public partial class FileHistoryWindow : Window
{
    public string? SelectedFilePath { get; private set; }

    private CacheService _cacheService = null!;

    public ObservableCollection<FileHistoryItem> HistoryItems { get; } = [];

    // Parameterless constructor required by Avalonia XAML compiler
    public FileHistoryWindow()
    {
        InitializeComponent();
        DataContext = this;
    }

    public FileHistoryWindow(CacheService cacheService)
    {
        _cacheService = cacheService;
        InitializeComponent();
        DataContext = this;
        LoadHistory();
    }

    private void LoadHistory()
    {
        HistoryItems.Clear();
        foreach (var entry in _cacheService.GetAllPreviewFileHistory())
        {
            HistoryItems.Add(new FileHistoryItem
            {
                FilePath = entry.FilePath,
                LastOpenedText = $"Последно отворен: {entry.LastOpenedAt.ToLocalTime():dd MMM yyyy HH:mm}"
            });
        }
    }

    private void OnOpenItem(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path })
        {
            SelectedFilePath = path;
            Close();
        }
    }

    private void OnDeleteItem(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path })
        {
            _cacheService.DeletePreviewFileHistory(path);
            LoadHistory();
            if (HistoryItems.Count == 0)
                Close();
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
