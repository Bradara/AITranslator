using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AITrans.Views;

/// <summary>
/// Small dialog for entering Title/Author/Topic/Date, used to add or edit the YAML front
/// matter block of a markdown file. Read <see cref="Saved"/> after ShowDialog returns —
/// false means the user cancelled and the fields should be ignored.
/// </summary>
public partial class YamlFrontMatterWindow : Window
{
    public string TitleText { get; set; } = "";
    public string AuthorText { get; set; } = "";
    public string TopicText { get; set; } = "";
    public DateTimeOffset? DateValue { get; set; }

    public bool Saved { get; private set; }

    // Parameterless constructor required by Avalonia XAML compiler
    public YamlFrontMatterWindow()
    {
        InitializeComponent();
        DataContext = this;
    }

    public YamlFrontMatterWindow(string title, string author, string topic, string date) : this()
    {
        TitleText = title;
        AuthorText = author;
        TopicText = topic;
        DateValue = DateTimeOffset.TryParse(date, out var parsed) ? parsed : DateTimeOffset.Now;
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        Saved = true;
        Close();
    }

    private void OnClear(object? sender, RoutedEventArgs e)
    {
        TitleText = "";
        AuthorText = "";
        TopicText = "";
        DateValue = null;
        Saved = true;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
}
