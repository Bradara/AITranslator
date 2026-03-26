using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using AITrans.Models;

namespace AITrans.Services;

public class SubtitleCacheInfo
{
    public string FilePath { get; init; } = "";
    public string Language { get; init; } = "";
    public DateTime SavedAt { get; init; }
    public int TotalEntries { get; init; }
    public int TranslatedEntries { get; init; }
}

public class MarkdownCacheInfo
{
    public string SessionKey { get; init; } = "";
    public string Language { get; init; } = "";
    public DateTime SavedAt { get; init; }
    public int TotalParagraphs { get; init; }
    public int TranslatedParagraphs { get; init; }
}

public class CacheService
{
    private readonly string _dbPath;

    public CacheService()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AITrans");
        Directory.CreateDirectory(dir);
        _dbPath = Path.Combine(dir, "cache.db");
        InitializeDatabase();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }

    private void InitializeDatabase()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS subtitle_sessions (
                file_path TEXT NOT NULL,
                language TEXT NOT NULL,
                saved_at TEXT NOT NULL,
                PRIMARY KEY (file_path)
            );
            CREATE TABLE IF NOT EXISTS subtitle_entries (
                file_path TEXT NOT NULL,
                entry_index INTEGER NOT NULL,
                start_time TEXT NOT NULL,
                end_time TEXT NOT NULL,
                original_text TEXT NOT NULL,
                translated_text TEXT NOT NULL,
                PRIMARY KEY (file_path, entry_index)
            );
            CREATE TABLE IF NOT EXISTS markdown_sessions (
                session_key TEXT NOT NULL,
                input_text TEXT NOT NULL,
                language TEXT NOT NULL,
                saved_at TEXT NOT NULL,
                PRIMARY KEY (session_key)
            );
            CREATE TABLE IF NOT EXISTS markdown_entries (
                session_key TEXT NOT NULL,
                paragraph_index INTEGER NOT NULL,
                original_text TEXT NOT NULL,
                translated_text TEXT NOT NULL,
                PRIMARY KEY (session_key, paragraph_index)
            );
            """;
        cmd.ExecuteNonQuery();
    }

    // ─── Subtitles ────────────────────────────────────────────────────────────

    public void SaveSubtitleSession(string filePath, string language, IEnumerable<SrtEntry> entries)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        Execute(conn, "DELETE FROM subtitle_sessions WHERE file_path = @fp",
            ("@fp", filePath));
        Execute(conn, "DELETE FROM subtitle_entries WHERE file_path = @fp",
            ("@fp", filePath));
        Execute(conn,
            "INSERT INTO subtitle_sessions (file_path, language, saved_at) VALUES (@fp, @lang, @at)",
            ("@fp", filePath), ("@lang", language), ("@at", DateTime.UtcNow.ToString("o")));

        foreach (var e in entries)
        {
            Execute(conn,
                """
                INSERT INTO subtitle_entries
                    (file_path, entry_index, start_time, end_time, original_text, translated_text)
                VALUES (@fp, @idx, @st, @et, @orig, @trans)
                """,
                ("@fp", filePath), ("@idx", e.Index),
                ("@st", e.StartTime), ("@et", e.EndTime),
                ("@orig", e.OriginalText), ("@trans", e.TranslatedText ?? ""));
        }

        tx.Commit();
    }

    public SubtitleCacheInfo? GetSubtitleCacheInfo(string filePath)
    {
        using var conn = Open();

        string lang;
        DateTime savedAt;

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT language, saved_at FROM subtitle_sessions WHERE file_path = @fp";
            cmd.Parameters.AddWithValue("@fp", filePath);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            lang = r.GetString(0);
            savedAt = DateTime.Parse(r.GetString(1));
        }

        using (var cmd2 = conn.CreateCommand())
        {
            cmd2.CommandText = "SELECT COUNT(*), SUM(CASE WHEN translated_text != '' THEN 1 ELSE 0 END) FROM subtitle_entries WHERE file_path = @fp";
            cmd2.Parameters.AddWithValue("@fp", filePath);
            using var r2 = cmd2.ExecuteReader();
            r2.Read();
            return new SubtitleCacheInfo
            {
                FilePath = filePath,
                Language = lang,
                SavedAt = savedAt,
                TotalEntries = r2.GetInt32(0),
                TranslatedEntries = r2.GetInt32(1)
            };
        }
    }

    public List<SrtEntry>? LoadSubtitleEntries(string filePath)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT entry_index, start_time, end_time, original_text, translated_text
            FROM subtitle_entries WHERE file_path = @fp ORDER BY entry_index
            """;
        cmd.Parameters.AddWithValue("@fp", filePath);
        using var r = cmd.ExecuteReader();

        var list = new List<SrtEntry>();
        while (r.Read())
        {
            list.Add(new SrtEntry
            {
                Index = r.GetInt32(0),
                StartTime = r.GetString(1),
                EndTime = r.GetString(2),
                OriginalText = r.GetString(3),
                TranslatedText = r.GetString(4)
            });
        }
        return list.Count == 0 ? null : list;
    }

    public void ClearSubtitleSession(string filePath)
    {
        using var conn = Open();
        Execute(conn, "DELETE FROM subtitle_sessions WHERE file_path = @fp", ("@fp", filePath));
        Execute(conn, "DELETE FROM subtitle_entries WHERE file_path = @fp", ("@fp", filePath));
    }

    /// <summary>Returns metadata for the most recently saved subtitle session, or null if none.</summary>
    public SubtitleCacheInfo? GetLatestSubtitleSession()
    {
        using var conn = Open();

        string filePath, lang;
        DateTime savedAt;

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT file_path, language, saved_at FROM subtitle_sessions ORDER BY saved_at DESC LIMIT 1";
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            filePath = r.GetString(0);
            lang = r.GetString(1);
            savedAt = DateTime.Parse(r.GetString(2));
        }

        using (var cmd2 = conn.CreateCommand())
        {
            cmd2.CommandText = "SELECT COUNT(*), SUM(CASE WHEN translated_text != '' THEN 1 ELSE 0 END) FROM subtitle_entries WHERE file_path = @fp";
            cmd2.Parameters.AddWithValue("@fp", filePath);
            using var r2 = cmd2.ExecuteReader();
            r2.Read();
            return new SubtitleCacheInfo
            {
                FilePath = filePath,
                Language = lang,
                SavedAt = savedAt,
                TotalEntries = r2.GetInt32(0),
                TranslatedEntries = r2.GetInt32(1)
            };
        }
    }

    // ─── Markdown ─────────────────────────────────────────────────────────────

    /// <summary>Single slot key used for markdown (always overwrite).</summary>
    public const string MarkdownSlot = "current";

    public void SaveMarkdownSession(string inputText, string language, IEnumerable<MarkdownEntry> paragraphs)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        Execute(conn, "DELETE FROM markdown_sessions WHERE session_key = @sk", ("@sk", MarkdownSlot));
        Execute(conn, "DELETE FROM markdown_entries WHERE session_key = @sk", ("@sk", MarkdownSlot));
        Execute(conn,
            "INSERT INTO markdown_sessions (session_key, input_text, language, saved_at) VALUES (@sk, @inp, @lang, @at)",
            ("@sk", MarkdownSlot), ("@inp", inputText), ("@lang", language), ("@at", DateTime.UtcNow.ToString("o")));

        foreach (var p in paragraphs)
        {
            Execute(conn,
                "INSERT INTO markdown_entries (session_key, paragraph_index, original_text, translated_text) VALUES (@sk, @idx, @orig, @trans)",
                ("@sk", MarkdownSlot), ("@idx", p.Index),
                ("@orig", p.OriginalText), ("@trans", p.TranslatedText ?? ""));
        }

        tx.Commit();
    }

    public MarkdownCacheInfo? GetMarkdownCacheInfo()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT language, saved_at FROM markdown_sessions WHERE session_key = @sk";
        cmd.Parameters.AddWithValue("@sk", MarkdownSlot);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;

        var lang = r.GetString(0);
        var savedAt = DateTime.Parse(r.GetString(1));

        using var cmd2 = conn.CreateCommand();
        cmd2.CommandText = "SELECT COUNT(*), SUM(CASE WHEN translated_text != '' THEN 1 ELSE 0 END) FROM markdown_entries WHERE session_key = @sk";
        cmd2.Parameters.AddWithValue("@sk", MarkdownSlot);
        using var r2 = cmd2.ExecuteReader();
        r2.Read();
        return new MarkdownCacheInfo
        {
            SessionKey = MarkdownSlot,
            Language = lang,
            SavedAt = savedAt,
            TotalParagraphs = r2.GetInt32(0),
            TranslatedParagraphs = r2.GetInt32(1)
        };
    }

    public (string inputText, List<MarkdownEntry>)? LoadMarkdownSession()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT input_text FROM markdown_sessions WHERE session_key = @sk";
        cmd.Parameters.AddWithValue("@sk", MarkdownSlot);
        var inputText = cmd.ExecuteScalar() as string;
        if (inputText == null) return null;

        using var cmd2 = conn.CreateCommand();
        cmd2.CommandText = "SELECT paragraph_index, original_text, translated_text FROM markdown_entries WHERE session_key = @sk ORDER BY paragraph_index";
        cmd2.Parameters.AddWithValue("@sk", MarkdownSlot);
        using var r = cmd2.ExecuteReader();
        var paragraphs = new List<MarkdownEntry>();
        while (r.Read())
        {
            paragraphs.Add(new MarkdownEntry
            {
                Index = r.GetInt32(0),
                OriginalText = r.GetString(1),
                TranslatedText = r.GetString(2)
            });
        }
        return (inputText, paragraphs);
    }

    public void ClearMarkdownSession()
    {
        using var conn = Open();
        Execute(conn, "DELETE FROM markdown_sessions WHERE session_key = @sk", ("@sk", MarkdownSlot));
        Execute(conn, "DELETE FROM markdown_entries WHERE session_key = @sk", ("@sk", MarkdownSlot));
    }

    // ─── Helper ───────────────────────────────────────────────────────────────

    private static void Execute(SqliteConnection conn, string sql, params (string, object)[] parameters)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }
}
