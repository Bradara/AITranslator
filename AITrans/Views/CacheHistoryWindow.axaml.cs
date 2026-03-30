using System.Collections.ObjectModel;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using AITrans.Services;

namespace AITrans.Views;

/// <summary>A single row in the cache history list.</summary>
public class SessionRow
{
    public string Key { get; init; } = "";
    public string FileName { get; init; } = "";
    public string FullPath { get; init; } = "";
    public string ProgressText { get; init; } = "";
}

/// <summary>
/// Dialog that shows all cached sessions and lets the user pick one to restore or delete.
/// Read <see cref="SelectedKey"/> after ShowDialog returns — null means cancelled.
/// </summary>
public partial class CacheHistoryWindow : Window
{
    public string? SelectedKey { get; private set; }

    private CacheService _cacheService = null!;
    private bool _isSubtitle;

    public ObservableCollection<SessionRow> Sessions { get; } = [];

    // Parameterless constructor required by Avalonia XAML compiler
    public CacheHistoryWindow()
    {
        InitializeComponent();
        DataContext = this;
    }

    public CacheHistoryWindow(CacheService cacheService, bool isSubtitle)
    {
        _cacheService = cacheService;
        _isSubtitle = isSubtitle;
        InitializeComponent();
        DataContext = this;
        LoadSessions();
    }

    private void LoadSessions()
    {
        Sessions.Clear();
        if (_isSubtitle)
        {
            foreach (var s in _cacheService.GetAllSubtitleSessions())
            {
                var name = Path.GetFileName(s.FilePath);
                Sessions.Add(new SessionRow
                {
                    Key = s.FilePath,
                    FileName = string.IsNullOrEmpty(name) ? s.FilePath : name,
                    FullPath = s.FilePath,
                    ProgressText = $"{s.TranslatedEntries}/{s.TotalEntries} субтитри преведени — {s.SavedAt.ToLocalTime():dd MMM yyyy HH:mm}"
                });
            }
        }
        else
        {
            foreach (var s in _cacheService.GetAllMarkdownSessions())
            {
                Sessions.Add(new SessionRow
                {
                    Key = s.SessionKey,
                    FileName = s.FileName,
                    FullPath = s.SessionKey is "unsaved" or "current" ? "(поставен текст)" : s.SessionKey,
                    ProgressText = $"{s.TranslatedParagraphs}/{s.TotalParagraphs} параграфа преведени — {s.SavedAt.ToLocalTime():dd MMM yyyy HH:mm}"
                });
            }
        }
    }

    private void OnRestoreItem(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string key })
        {
            SelectedKey = key;
            Close();
        }
    }

    private void OnDeleteItem(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string key })
        {
            if (_isSubtitle)
                _cacheService.ClearSubtitleSession(key);
            else
                _cacheService.ClearMarkdownSession(key);
            LoadSessions();
            if (Sessions.Count == 0)
                Close();
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
