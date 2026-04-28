using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using AITrans.Models;

namespace AITrans.Services;

public class SettingsService
{
    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AITrans");

    private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");
    private static readonly string ChatHistoryPath = Path.Combine(SettingsDir, "chat-history.json");

    private const int MaxMessagesPerKey = 100;

    public AppSettings Settings { get; private set; } = new();

    public void Load()
    {
        if (!File.Exists(SettingsPath)) return;

        var json = File.ReadAllText(SettingsPath);
        Settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(SettingsDir);
        var json = JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath, json);
    }

    // ──────────────────────────────────────────────────────────────────────────
    //  Chat history persistence
    // ──────────────────────────────────────────────────────────────────────────

    public Dictionary<string, List<ChatMessage>> LoadAllChatHistory()
    {
        if (!File.Exists(ChatHistoryPath))
            return [];

        try
        {
            var json = File.ReadAllText(ChatHistoryPath);
            return JsonSerializer.Deserialize<Dictionary<string, List<ChatMessage>>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public void SaveChatHistory(string fileKey, List<ChatMessage> messages)
    {
        Directory.CreateDirectory(SettingsDir);
        var all = LoadAllChatHistory();

        // Keep only the last MaxMessagesPerKey messages for this key
        var trimmed = messages.Count > MaxMessagesPerKey
            ? messages.GetRange(messages.Count - MaxMessagesPerKey, MaxMessagesPerKey)
            : messages;

        all[fileKey] = trimmed;

        var json = JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ChatHistoryPath, json);
    }
}
