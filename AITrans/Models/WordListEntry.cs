using System;

namespace AITrans.Models;

public class WordListEntry
{
    public int Id { get; set; }
    public string Word { get; set; } = "";
    public string SourceFile { get; set; } = "";
    public DateTime AddedAt { get; set; }
}
