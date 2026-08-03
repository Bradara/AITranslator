using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AITrans.Models;
using AITrans.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AITrans.ViewModels;

public partial class AiChatViewModel : ViewModelBase
{
    private readonly TranslationService _translationService;
    private readonly SettingsService _settingsService;
    private CancellationTokenSource? _sendCts;

    // How many previous messages from the current session are sent as context with each request.
    private const int ContextWindowMessages = 20;

    public ObservableCollection<ChatSession> Sessions { get; } = [];
    public ObservableCollection<ChatMessage> CurrentMessages { get; } = [];

    public List<AiProvider> AvailableProviders { get; }

    [ObservableProperty] private ChatSession? _selectedSession;
    [ObservableProperty] private AiProvider _selectedProvider;
    [ObservableProperty] private string _selectedModel = "";
    [ObservableProperty] private List<string> _availableModels = [];
    [ObservableProperty] private string _chatInput = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private int _scrollRequest;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasStatus))] private string _statusText = "";

    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusText);

    public AiChatViewModel(TranslationService translationService, SettingsService settingsService)
    {
        _translationService = translationService;
        _settingsService = settingsService;

        AvailableProviders = Enum.GetValues<AiProvider>()
            .Where(p => _settingsService.Settings.IsProviderConfigured(p))
            .ToList();
        if (AvailableProviders.Count == 0)
            AvailableProviders = [AiProvider.OpenAI];

        foreach (var session in _settingsService.LoadChatSessions())
            Sessions.Add(session);

        if (Sessions.Count > 0)
            SelectedSession = Sessions[0];
        else
            NewSession();
    }

    partial void OnSelectedSessionChanged(ChatSession? value)
    {
        CurrentMessages.Clear();
        if (value == null) return;

        foreach (var msg in value.Messages)
            CurrentMessages.Add(msg);

        SelectedProvider = AvailableProviders.Contains(value.Provider) ? value.Provider : AvailableProviders[0];
        RefreshAvailableModels(keepModel: value.Model);
        ScrollRequest++;
    }

    partial void OnSelectedProviderChanged(AiProvider value)
    {
        RefreshAvailableModels();
        if (SelectedSession != null)
            SelectedSession.Provider = value;
    }

    partial void OnSelectedModelChanged(string value)
    {
        if (SelectedSession != null)
            SelectedSession.Model = value;
    }

    partial void OnChatInputChanged(string value) => SendCommand.NotifyCanExecuteChanged();

    private void RefreshAvailableModels(string? keepModel = null)
    {
        var settings = _settingsService.Settings;
        var options = settings.GetChatModelOptions(SelectedProvider)
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Distinct()
            .ToList();
        if (options.Count == 0)
            options = [settings.GetChatModel(SelectedProvider)];

        AvailableModels = options;
        SelectedModel = !string.IsNullOrWhiteSpace(keepModel) && options.Contains(keepModel)
            ? keepModel
            : options[0];
    }

    [RelayCommand]
    private void NewSession()
    {
        var settings = _settingsService.Settings;
        var provider = AvailableProviders.Contains(SelectedProvider) ? SelectedProvider : AvailableProviders[0];
        var session = new ChatSession
        {
            Provider = provider,
            Model = settings.GetChatModel(provider)
        };

        Sessions.Insert(0, session);
        SelectedSession = session;
        PersistSessions();
    }

    [RelayCommand]
    private void DeleteSession(ChatSession? session)
    {
        session ??= SelectedSession;
        if (session == null) return;

        var index = Sessions.IndexOf(session);
        if (index < 0) return;

        Sessions.RemoveAt(index);

        if (Sessions.Count == 0)
            NewSession();
        else if (SelectedSession == session)
            SelectedSession = Sessions[Math.Min(index, Sessions.Count - 1)];

        PersistSessions();
    }

    private bool CanSend() => !IsBusy && !string.IsNullOrWhiteSpace(ChatInput) && SelectedSession != null;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var session = SelectedSession;
        if (session == null) return;

        var settings = _settingsService.Settings;
        var provider = SelectedProvider;
        var apiKey = settings.GetProviderApiKey(provider);
        if (settings.ProviderRequiresApiKey(provider) && string.IsNullOrWhiteSpace(apiKey))
        {
            StatusText = $"Липсва API ключ за {AppSettings.DisplayName(provider)} — добави го в Настройки.";
            return;
        }

        StatusText = "";
        var userMessage = ChatInput.Trim();
        ChatInput = "";

        var userMsg = new ChatMessage { Role = ChatRole.User, Content = userMessage };
        CurrentMessages.Add(userMsg);
        session.Messages.Add(userMsg);
        ScrollRequest++;

        if (session.Title == "Нова сесия")
            session.Title = userMessage.Length > 40 ? userMessage[..40] + "…" : userMessage;

        IsBusy = true;
        SendCommand.NotifyCanExecuteChanged();
        _sendCts?.Dispose();
        _sendCts = new CancellationTokenSource();

        try
        {
            var endpoint = settings.GetProviderEndpoint(provider);
            var model = SelectedModel;
            // Context from the recent conversation, excluding the message just added (sent separately).
            var history = session.Messages
                .Take(session.Messages.Count - 1)
                .TakeLast(ContextWindowMessages)
                .ToList();

            const string systemPrompt = "You are a helpful assistant. Answer clearly and concisely, using Markdown formatting where useful.";

            var reply = await _translationService.ChatWithHistoryAsync(
                systemPrompt, history, userMessage, provider, apiKey, model, endpoint, settings.Temperature, _sendCts.Token);

            var replyMsg = new ChatMessage { Role = ChatRole.Assistant, Content = reply };
            CurrentMessages.Add(replyMsg);
            session.Messages.Add(replyMsg);
            ScrollRequest++;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            var errMsg = new ChatMessage { Role = ChatRole.Assistant, Content = $"⚠️ Грешка: {ex.Message}" };
            CurrentMessages.Add(errMsg);
            session.Messages.Add(errMsg);
            ScrollRequest++;
        }
        finally
        {
            session.UpdatedAt = DateTime.UtcNow;
            IsBusy = false;
            SendCommand.NotifyCanExecuteChanged();
            PersistSessions();
        }
    }

    private void PersistSessions() => _settingsService.SaveChatSessions(Sessions.ToList());

    public void PersistSessionState() => PersistSessions();
}
