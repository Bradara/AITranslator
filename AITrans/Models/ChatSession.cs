using System;
using System.Collections.Generic;

namespace AITrans.Models;

public class ChatSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "Нова сесия";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public AiProvider Provider { get; set; } = AiProvider.OpenAI;
    public string Model { get; set; } = "";
    public List<ChatMessage> Messages { get; set; } = [];
}
