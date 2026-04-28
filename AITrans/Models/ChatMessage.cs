using System;
using System.Text.Json.Serialization;

namespace AITrans.Models;

public enum ChatRole { User, Assistant }

public class ChatMessage
{
    public ChatRole Role { get; set; }
    public string Content { get; set; } = "";
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public bool IsUser => Role == ChatRole.User;

    [JsonIgnore]
    public bool IsAssistant => Role == ChatRole.Assistant;
}
