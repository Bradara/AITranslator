using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
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
    public string FileName => SessionKey is "current" or "unsaved"
        ? "(поставен текст)"
        : Path.GetFileName(SessionKey);
    public string Language { get; init; } = "";
    public DateTime SavedAt { get; init; }
    public int TotalParagraphs { get; init; }
    public int TranslatedParagraphs { get; init; }
}

public class PreviewFileHistoryInfo
{
    public string FilePath { get; init; } = "";
    public string FileName => Path.GetFileName(FilePath);
    public DateTime LastOpenedAt { get; init; }
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
            CREATE TABLE IF NOT EXISTS preview_file_history (
                file_path TEXT NOT NULL PRIMARY KEY,
                last_opened_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS flashcards (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                front_text TEXT NOT NULL DEFAULT '',
                back_text TEXT NOT NULL DEFAULT '',
                usage_text TEXT NOT NULL DEFAULT '',
                correct_count INTEGER NOT NULL DEFAULT 0,
                wrong_count INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS word_list (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                word TEXT NOT NULL,
                source_file TEXT NOT NULL DEFAULT '',
                added_at TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();

        // Migrate: add rating column if it doesn't exist yet (safe for existing databases)
        try
        {
            using var migCmd = conn.CreateCommand();
            migCmd.CommandText = "ALTER TABLE flashcards ADD COLUMN rating INTEGER NOT NULL DEFAULT 0";
            migCmd.ExecuteNonQuery();
        }
        catch { /* column already exists – ignore */ }
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

    /// <summary>Returns all cached subtitle sessions sorted by most recent first.</summary>
    public List<SubtitleCacheInfo> GetAllSubtitleSessions()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.file_path, s.language, s.saved_at,
                   COUNT(e.entry_index),
                   COALESCE(SUM(CASE WHEN e.translated_text != '' THEN 1 ELSE 0 END), 0)
            FROM subtitle_sessions s
            LEFT JOIN subtitle_entries e ON s.file_path = e.file_path
            GROUP BY s.file_path, s.language, s.saved_at
            ORDER BY s.saved_at DESC
            """;
        using var r = cmd.ExecuteReader();
        var list = new List<SubtitleCacheInfo>();
        while (r.Read())
        {
            list.Add(new SubtitleCacheInfo
            {
                FilePath = r.GetString(0),
                Language = r.GetString(1),
                SavedAt = DateTime.Parse(r.GetString(2)),
                TotalEntries = r.GetInt32(3),
                TranslatedEntries = r.GetInt32(4)
            });
        }
        return list;
    }

    // ─── Markdown ─────────────────────────────────────────────────────────────

    public void SaveMarkdownSession(string filePath, string inputText, string language, IEnumerable<MarkdownEntry> paragraphs)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        Execute(conn, "DELETE FROM markdown_sessions WHERE session_key = @sk", ("@sk", filePath));
        Execute(conn, "DELETE FROM markdown_entries WHERE session_key = @sk", ("@sk", filePath));
        Execute(conn,
            "INSERT INTO markdown_sessions (session_key, input_text, language, saved_at) VALUES (@sk, @inp, @lang, @at)",
            ("@sk", filePath), ("@inp", inputText), ("@lang", language), ("@at", DateTime.UtcNow.ToString("o")));

        foreach (var p in paragraphs)
        {
            Execute(conn,
                "INSERT INTO markdown_entries (session_key, paragraph_index, original_text, translated_text) VALUES (@sk, @idx, @orig, @trans)",
                ("@sk", filePath), ("@idx", p.Index),
                ("@orig", p.OriginalText), ("@trans", p.TranslatedText ?? ""));
        }

        tx.Commit();
    }

    public MarkdownCacheInfo? GetMarkdownCacheInfo(string filePath)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT language, saved_at FROM markdown_sessions WHERE session_key = @sk";
        cmd.Parameters.AddWithValue("@sk", filePath);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;

        var lang = r.GetString(0);
        var savedAt = DateTime.Parse(r.GetString(1));

        using var cmd2 = conn.CreateCommand();
        cmd2.CommandText = "SELECT COUNT(*), COALESCE(SUM(CASE WHEN translated_text != '' THEN 1 ELSE 0 END), 0) FROM markdown_entries WHERE session_key = @sk";
        cmd2.Parameters.AddWithValue("@sk", filePath);
        using var r2 = cmd2.ExecuteReader();
        r2.Read();
        return new MarkdownCacheInfo
        {
            SessionKey = filePath,
            Language = lang,
            SavedAt = savedAt,
            TotalParagraphs = r2.GetInt32(0),
            TranslatedParagraphs = r2.GetInt32(1)
        };
    }

    public (string inputText, List<MarkdownEntry>)? LoadMarkdownSession(string filePath)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT input_text FROM markdown_sessions WHERE session_key = @sk";
        cmd.Parameters.AddWithValue("@sk", filePath);
        var inputText = cmd.ExecuteScalar() as string;
        if (inputText == null) return null;

        using var cmd2 = conn.CreateCommand();
        cmd2.CommandText = "SELECT paragraph_index, original_text, translated_text FROM markdown_entries WHERE session_key = @sk ORDER BY paragraph_index";
        cmd2.Parameters.AddWithValue("@sk", filePath);
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

    public void ClearMarkdownSession(string filePath)
    {
        using var conn = Open();
        Execute(conn, "DELETE FROM markdown_sessions WHERE session_key = @sk", ("@sk", filePath));
        Execute(conn, "DELETE FROM markdown_entries WHERE session_key = @sk", ("@sk", filePath));
    }

    /// <summary>Returns all cached markdown sessions sorted by most recent first.</summary>
    public List<MarkdownCacheInfo> GetAllMarkdownSessions()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.session_key, s.language, s.saved_at,
                   COUNT(e.paragraph_index),
                   COALESCE(SUM(CASE WHEN e.translated_text != '' THEN 1 ELSE 0 END), 0)
            FROM markdown_sessions s
            LEFT JOIN markdown_entries e ON s.session_key = e.session_key
            GROUP BY s.session_key, s.language, s.saved_at
            ORDER BY s.saved_at DESC
            """;
        using var r = cmd.ExecuteReader();
        var list = new List<MarkdownCacheInfo>();
        while (r.Read())
        {
            list.Add(new MarkdownCacheInfo
            {
                SessionKey = r.GetString(0),
                Language = r.GetString(1),
                SavedAt = DateTime.Parse(r.GetString(2)),
                TotalParagraphs = r.GetInt32(3),
                TranslatedParagraphs = r.GetInt32(4)
            });
        }
        return list;
    }

    /// <summary>Returns metadata for the most recently saved markdown session, or null if none.</summary>
    public MarkdownCacheInfo? GetLatestMarkdownSession()
    {
        var all = GetAllMarkdownSessions();
        return all.Count > 0 ? all[0] : null;
    }

    // ─── Preview File History ─────────────────────────────────────────────────

    public void UpsertPreviewFileHistory(string filePath)
    {
        using var conn = Open();
        Execute(conn,
            "INSERT OR REPLACE INTO preview_file_history (file_path, last_opened_at) VALUES (@fp, @at)",
            ("@fp", filePath), ("@at", DateTime.UtcNow.ToString("o")));
    }

    public List<PreviewFileHistoryInfo> GetAllPreviewFileHistory()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT file_path, last_opened_at FROM preview_file_history ORDER BY last_opened_at DESC";
        using var r = cmd.ExecuteReader();
        var list = new List<PreviewFileHistoryInfo>();
        while (r.Read())
        {
            list.Add(new PreviewFileHistoryInfo
            {
                FilePath = r.GetString(0),
                LastOpenedAt = DateTime.Parse(r.GetString(1))
            });
        }
        return list;
    }

    public void DeletePreviewFileHistory(string filePath)
    {
        using var conn = Open();
        Execute(conn, "DELETE FROM preview_file_history WHERE file_path = @fp", ("@fp", filePath));
    }

    // ─── Flashcards ───────────────────────────────────────────────────────────

    public List<FlashCard> GetAllFlashCards()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, front_text, back_text, usage_text, correct_count, wrong_count, created_at, rating
            FROM flashcards ORDER BY id
            """;
        using var r = cmd.ExecuteReader();
        var list = new List<FlashCard>();
        while (r.Read())
        {
            list.Add(new FlashCard
            {
                Id           = r.GetInt32(0),
                FrontText    = r.GetString(1),
                BackText     = r.GetString(2),
                UsageText    = r.GetString(3),
                CorrectCount = r.GetInt32(4),
                WrongCount   = r.GetInt32(5),
                CreatedAt    = DateTime.Parse(r.GetString(6)),
                Rating       = (CardRating)r.GetInt32(7)
            });
        }
        return list;
    }

    /// <summary>Inserts a new card and returns its auto-generated id.</summary>
    public int SaveFlashCard(FlashCard card)
    {
        using var conn = Open();
        Execute(conn,
            """
            INSERT INTO flashcards (front_text, back_text, usage_text, correct_count, wrong_count, created_at, rating)
            VALUES (@ft, @bt, @ut, @cc, @wc, @ca, @rt)
            """,
            ("@ft", card.FrontText), ("@bt", card.BackText), ("@ut", card.UsageText),
            ("@cc", card.CorrectCount), ("@wc", card.WrongCount),
            ("@ca", card.CreatedAt.ToString("o")), ("@rt", (int)card.Rating));

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT last_insert_rowid()";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void DeleteFlashCard(int id)
    {
        using var conn = Open();
        Execute(conn, "DELETE FROM flashcards WHERE id = @id", ("@id", id));
    }

    public void UpdateFlashCardStats(int id, bool correct)
    {
        using var conn = Open();
        if (correct)
            Execute(conn, "UPDATE flashcards SET correct_count = correct_count + 1 WHERE id = @id", ("@id", id));
        else
            Execute(conn, "UPDATE flashcards SET wrong_count = wrong_count + 1 WHERE id = @id", ("@id", id));
    }

    public void UpdateFlashCard(FlashCard card)
    {
        using var conn = Open();
        Execute(conn,
            """
            UPDATE flashcards
            SET front_text = @ft, back_text = @bt, usage_text = @ut, rating = @rt
            WHERE id = @id
            """,
            ("@ft", card.FrontText), ("@bt", card.BackText),
            ("@ut", card.UsageText), ("@rt", (int)card.Rating), ("@id", card.Id));
    }

    public void UpdateFlashCardRating(int id, CardRating rating)
    {
        using var conn = Open();
        Execute(conn, "UPDATE flashcards SET rating = @rt WHERE id = @id",
            ("@rt", (int)rating), ("@id", id));
    }

    // ─── Word List ─────────────────────────────────────────────────────────

    public int SaveWordListEntry(WordListEntry entry)
    {
        using var conn = Open();
        Execute(conn,
            "INSERT INTO word_list (word, source_file, added_at) VALUES (@w, @sf, @at)",
            ("@w", entry.Word), ("@sf", entry.SourceFile), ("@at", entry.AddedAt.ToString("o")));

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT last_insert_rowid()";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public List<WordListEntry> GetAllWordListEntries()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, word, source_file, added_at FROM word_list ORDER BY added_at DESC";
        using var r = cmd.ExecuteReader();
        var list = new List<WordListEntry>();
        while (r.Read())
        {
            list.Add(new WordListEntry
            {
                Id = r.GetInt32(0),
                Word = r.GetString(1),
                SourceFile = r.GetString(2),
                AddedAt = DateTime.Parse(r.GetString(3))
            });
        }
        return list;
    }

    public void DeleteWordListEntry(int id)
    {
        using var conn = Open();
        Execute(conn, "DELETE FROM word_list WHERE id = @id", ("@id", id));
    }

    public void ClearWordList()
    {
        using var conn = Open();
        Execute(conn, "DELETE FROM word_list");
    }

    // ─── CSV Export / Import (subtitle & markdown sessions) ────────────────────

    /// <summary>Exports a single subtitle session (all its entries) to a semicolon-delimited CSV file.</summary>
    public async Task ExportSubtitleSessionToCsvAsync(string sessionKey, string csvPath)
    {
        var info = GetSubtitleCacheInfo(sessionKey);
        var entries = LoadSubtitleEntries(sessionKey) ?? new List<SrtEntry>();

        var sb = new StringBuilder();
        sb.AppendLine($"#file_path={EscapeMeta(sessionKey)}");
        sb.AppendLine($"#language={EscapeMeta(info?.Language ?? "")}");
        sb.AppendLine("index;start_time;end_time;original_text;translated_text");
        foreach (var e in entries)
        {
            sb.Append(e.Index).Append(';');
            sb.Append(QuoteCsvField(e.StartTime, ';')).Append(';');
            sb.Append(QuoteCsvField(e.EndTime, ';')).Append(';');
            sb.Append(QuoteCsvField(e.OriginalText, ';')).Append(';');
            sb.AppendLine(QuoteCsvField(e.TranslatedText ?? "", ';'));
        }

        await File.WriteAllTextAsync(csvPath, sb.ToString(), Encoding.UTF8);
    }

    /// <summary>
    /// Imports a subtitle session from a CSV file produced by <see cref="ExportSubtitleSessionToCsvAsync"/>
    /// and saves it into the cache, overwriting any existing session with the same key.
    /// Returns the file_path key it was saved under.
    /// </summary>
    public async Task<string> ImportSubtitleSessionFromCsvAsync(string csvPath)
    {
        var lines = await File.ReadAllLinesAsync(csvPath, Encoding.UTF8);
        var sessionKey = Path.GetFileNameWithoutExtension(csvPath);
        var language = "";
        var entries = new List<SrtEntry>();
        var headerSkipped = false;

        foreach (var raw in lines)
        {
            if (raw.Length == 0) continue;
            if (raw.StartsWith("#file_path=")) { sessionKey = UnescapeMeta(raw["#file_path=".Length..]); continue; }
            if (raw.StartsWith("#language=")) { language = UnescapeMeta(raw["#language=".Length..]); continue; }
            if (!headerSkipped) { headerSkipped = true; continue; }

            var parts = SplitCsvLine(raw, ';');
            if (parts.Length < 5) continue;
            entries.Add(new SrtEntry
            {
                Index = int.TryParse(parts[0], out var idx) ? idx : entries.Count,
                StartTime = parts[1],
                EndTime = parts[2],
                OriginalText = parts[3],
                TranslatedText = parts[4]
            });
        }

        SaveSubtitleSession(sessionKey, language, entries);
        return sessionKey;
    }

    /// <summary>Exports a single markdown session (all its paragraphs) to a semicolon-delimited CSV file.</summary>
    public async Task ExportMarkdownSessionToCsvAsync(string sessionKey, string csvPath)
    {
        var result = LoadMarkdownSession(sessionKey);
        var (inputText, paragraphs) = result ?? ("", new List<MarkdownEntry>());
        var info = GetMarkdownCacheInfo(sessionKey);

        var sb = new StringBuilder();
        sb.AppendLine($"#session_key={EscapeMeta(sessionKey)}");
        sb.AppendLine($"#language={EscapeMeta(info?.Language ?? "")}");
        sb.AppendLine($"#input_text={EscapeMeta(inputText)}");
        sb.AppendLine("paragraph_index;original_text;translated_text");
        foreach (var p in paragraphs)
        {
            sb.Append(p.Index).Append(';');
            sb.Append(QuoteCsvField(p.OriginalText, ';')).Append(';');
            sb.AppendLine(QuoteCsvField(p.TranslatedText ?? "", ';'));
        }

        await File.WriteAllTextAsync(csvPath, sb.ToString(), Encoding.UTF8);
    }

    /// <summary>
    /// Imports a markdown session from a CSV file produced by <see cref="ExportMarkdownSessionToCsvAsync"/>
    /// and saves it into the cache, overwriting any existing session with the same key.
    /// Returns the session_key it was saved under.
    /// </summary>
    public async Task<string> ImportMarkdownSessionFromCsvAsync(string csvPath)
    {
        var lines = await File.ReadAllLinesAsync(csvPath, Encoding.UTF8);
        var sessionKey = Path.GetFileNameWithoutExtension(csvPath);
        var language = "";
        var inputText = "";
        var paragraphs = new List<MarkdownEntry>();
        var headerSkipped = false;

        foreach (var raw in lines)
        {
            if (raw.Length == 0) continue;
            if (raw.StartsWith("#session_key=")) { sessionKey = UnescapeMeta(raw["#session_key=".Length..]); continue; }
            if (raw.StartsWith("#language=")) { language = UnescapeMeta(raw["#language=".Length..]); continue; }
            if (raw.StartsWith("#input_text=")) { inputText = UnescapeMeta(raw["#input_text=".Length..]); continue; }
            if (!headerSkipped) { headerSkipped = true; continue; }

            var parts = SplitCsvLine(raw, ';');
            if (parts.Length < 3) continue;
            paragraphs.Add(new MarkdownEntry
            {
                Index = int.TryParse(parts[0], out var idx) ? idx : paragraphs.Count,
                OriginalText = parts[1],
                TranslatedText = parts[2]
            });
        }

        SaveMarkdownSession(sessionKey, inputText, language, paragraphs);
        return sessionKey;
    }

    /// <summary>Escapes backslashes and line breaks so a value can safely live on a single "#key=value" meta line.</summary>
    private static string EscapeMeta(string s) =>
        s.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n");

    private static string UnescapeMeta(string s)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                i++;
                sb.Append(s[i] switch { 'n' => '\n', 'r' => '\r', '\\' => '\\', _ => s[i] });
            }
            else sb.Append(s[i]);
        }
        return sb.ToString();
    }

    /// <summary>Wraps a field in double-quotes if it contains the delimiter, quotes, or newlines.</summary>
    private static string QuoteCsvField(string field, char delimiter)
    {
        if (field.Contains(delimiter) || field.Contains('"') || field.Contains('\n') || field.Contains('\r'))
            return "\"" + field.Replace("\"", "\"\"") + "\"";
        return field;
    }

    /// <summary>Simple RFC-4180-compatible CSV line splitter for a given delimiter.</summary>
    private static string[] SplitCsvLine(string line, char delimiter)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            else
            {
                if (c == '"')
                    inQuotes = true;
                else if (c == delimiter)
                {
                    fields.Add(sb.ToString());
                    sb.Clear();
                }
                else
                {
                    sb.Append(c);
                }
            }
        }
        fields.Add(sb.ToString());
        return fields.ToArray();
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
