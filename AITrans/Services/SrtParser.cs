using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using AITrans.Models;

namespace AITrans.Services;

public static class SrtParser
{
    public static List<SrtEntry> Parse(string filePath)
    {
        var entries = new List<SrtEntry>();
        var content = File.ReadAllText(filePath, Encoding.UTF8).TrimStart('\uFEFF');
        var blocks = Regex.Split(content.Trim(), @"\r?\n\r?\n");

        foreach (var block in blocks)
        {
            var lines = block.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length < 3) continue;

            if (!int.TryParse(lines[0].Trim(), out var index)) continue;

            var timeParts = lines[1].Trim().Split(" --> ");
            if (timeParts.Length != 2) continue;

            var text = string.Join("\n", lines[2..]).Trim();

            entries.Add(new SrtEntry
            {
                Index = index,
                StartTime = timeParts[0].Trim(),
                EndTime = timeParts[1].Trim(),
                OriginalText = text
            });
        }

        return entries;
    }

    public static void Write(string filePath, List<SrtEntry> entries)
    {
        var sb = new StringBuilder();
        foreach (var entry in entries)
        {
            sb.AppendLine(entry.Index.ToString());
            sb.AppendLine($"{entry.StartTime} --> {entry.EndTime}");
            sb.AppendLine(string.IsNullOrEmpty(entry.TranslatedText) ? entry.OriginalText : entry.TranslatedText);
            sb.AppendLine();
        }
        File.WriteAllText(filePath, sb.ToString(), new UTF8Encoding(false));
    }
}
