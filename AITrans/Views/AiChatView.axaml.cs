using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AITrans.Models;
using AITrans.ViewModels;

namespace AITrans.Views;

public partial class AiChatView : UserControl
{
    private AiChatViewModel? _subscribedVm;

    public AiChatView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_subscribedVm != null)
            _subscribedVm.PropertyChanged -= OnVmPropertyChanged;

        _subscribedVm = DataContext as AiChatViewModel;

        if (_subscribedVm != null)
            _subscribedVm.PropertyChanged += OnVmPropertyChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (DataContext is AiChatViewModel vm)
            vm.PersistSessionState();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AiChatViewModel.ScrollRequest))
            Dispatcher.UIThread.Post(() => ChatScrollViewer.ScrollToEnd(), DispatcherPriority.Background);
    }

    private void OnChatInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        if (DataContext is AiChatViewModel vm && vm.SendCommand.CanExecute(null))
        {
            vm.SendCommand.Execute(null);
            e.Handled = true;
        }
    }

    // Chat bubble right-click menu — copies whatever's selected across the chat history;
    // with nothing selected, falls back to copying the whole right-clicked message.
    private async void OnCopyChatSelectionClick(object? sender, RoutedEventArgs e)
    {
        var text = GetChatSelectedText();
        if (string.IsNullOrWhiteSpace(text) && sender is MenuItem { DataContext: ChatMessage msg })
            text = msg.Content;

        if (string.IsNullOrWhiteSpace(text))
        {
            if (DataContext is AiChatViewModel vm) vm.StatusText = "Няма текст за копиране.";
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) return;

        await clipboard.SetTextAsync(text);
    }

    private string? GetChatSelectedText()
    {
        var selectableParts = ChatScrollViewer.GetVisualDescendants()
            .OfType<SelectableTextBlock>()
            .Select(stb => stb.SelectedText)
            .Where(t => !string.IsNullOrEmpty(t))
            .ToList();
        if (selectableParts.Count > 0)
            return string.Join(" ", selectableParts).Trim();

        foreach (var desc in ChatScrollViewer.GetVisualDescendants())
        {
            var prop = desc.GetType().GetProperty("SelectedText");
            if (prop?.GetValue(desc) is string s && !string.IsNullOrEmpty(s))
                return s.Trim();
        }
        return null;
    }
}
