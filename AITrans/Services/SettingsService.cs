using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
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

    /// <summary>App-specific entropy for DPAPI so settings.json can't be decrypted by other
    /// tools that simply call CryptUnprotectData for the current user with no entropy.</summary>
    private static readonly byte[] ProtectionEntropy = Encoding.UTF8.GetBytes("AITrans.Settings.v1");

    public AppSettings Settings { get; private set; } = new();

    public void Load()
    {
        if (!File.Exists(SettingsPath)) return;

        var json = ReadProtected(SettingsPath);
        Settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(SettingsDir);
        var json = JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true });
        WriteProtected(SettingsPath, json);
    }

    /// <summary>
    /// Reads a settings file that may be DPAPI-encrypted (current Windows user) or plain UTF-8 JSON
    /// (older app versions, or non-Windows platforms where DPAPI is unavailable).
    /// </summary>
    private static string ReadProtected(string path)
    {
        var bytes = File.ReadAllBytes(path);

        if (OperatingSystem.IsWindows())
        {
            try
            {
                var decrypted = ProtectedData.Unprotect(bytes, ProtectionEntropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(decrypted);
            }
            catch (CryptographicException)
            {
                // Pre-existing plaintext file from an older version — fall through and read as-is.
                // The next Save() will re-write it encrypted.
            }
        }

        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// Writes JSON encrypted with DPAPI for the current Windows user. On platforms where DPAPI
    /// is unavailable, the file is written as plain UTF-8 JSON (unchanged previous behavior).
    /// </summary>
    private static void WriteProtected(string path, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);

        if (OperatingSystem.IsWindows())
            bytes = ProtectedData.Protect(bytes, ProtectionEntropy, DataProtectionScope.CurrentUser);

        File.WriteAllBytes(path, bytes);
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
