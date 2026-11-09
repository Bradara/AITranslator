using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AITrans.Models;
using GitHub.Copilot;

namespace AITrans.Services;

/// <summary>
/// Talks to GitHub Copilot through the locally installed GitHub Copilot CLI, using the official
/// GitHub.Copilot.SDK (JSON-RPC over the CLI's stdio). Authentication comes from whatever the CLI
/// is already logged in with (<c>copilot login</c> / <c>gh auth</c>), so no token is stored here.
/// One CLI process is started lazily and shared by every call; each call gets its own throwaway
/// session that is deleted afterwards so translations don't pile up in ~/.copilot/session-state.
/// </summary>
public sealed class CopilotCliService : IAsyncDisposable
{
    /// <summary>Sentinel stored in <see cref="AppSettings.GitHubCopilotInferenceUrl"/> to route Copilot calls through the local CLI.</summary>
    public const string CliEndpoint = "copilot-cli://local";

    public static bool IsCliEndpoint(string? endpoint) =>
        string.Equals(endpoint, CliEndpoint, StringComparison.OrdinalIgnoreCase);

    /// <summary>The CLI can only be spawned as a child process on desktop platforms.</summary>
    public static bool IsSupported =>
        OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    public record CliStatus(string CliPath, string Version, bool IsAuthenticated, string Login, string Message);

    // A translation batch on a reasoning model can easily take longer than the SDK's 60 s default.
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromMinutes(10);

    private readonly SemaphoreSlim _clientLock = new(1, 1);
    private CopilotClient? _client;
    private string? _clientCliPath;

    /// <summary>Optional user-supplied path to the CLI; empty means auto-detect.</summary>
    public string CliPathOverride { get; set; } = "";

    // ── CLI discovery ────────────────────────────────────────────────────────

    /// <summary>
    /// Finds the Copilot CLI executable: the explicit override, COPILOT_CLI_PATH, then PATH and the
    /// usual npm / winget / Homebrew install locations. On Windows the npm install only puts a
    /// copilot.cmd shim on PATH (which can't be spawned directly), so this resolves the native
    /// copilot.exe from the platform package next to it instead.
    /// </summary>
    public static string? ResolveCliPath(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            var p = Environment.ExpandEnvironmentVariables(overridePath.Trim().Trim('"'));
            return ResolveCandidate(p) ?? (Directory.Exists(p) ? ResolveInDirectory(p) : null);
        }

        var fromEnv = Environment.GetEnvironmentVariable("COPILOT_CLI_PATH");
        if (!string.IsNullOrWhiteSpace(fromEnv) && ResolveCandidate(fromEnv) is { } envPath)
            return envPath;

        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links"));
        }
        else
        {
            // GUI apps on macOS/Linux often don't inherit the shell's PATH.
            dirs.AddRange(["/opt/homebrew/bin", "/usr/local/bin", "/usr/bin", Path.Combine(home, ".local", "bin"), Path.Combine(home, ".npm-global", "bin")]);
        }

        foreach (var dir in dirs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (ResolveInDirectory(dir) is { } found)
                return found;
        }
        return null;
    }

    private static string? ResolveInDirectory(string dir)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var exe = Path.Combine(dir, "copilot.exe");
                if (File.Exists(exe)) return exe;
                if (File.Exists(Path.Combine(dir, "copilot.cmd")) || File.Exists(Path.Combine(dir, "copilot.ps1")))
                    return FindNpmNativeBinary(dir);
                return null;
            }

            var bin = Path.Combine(dir, "copilot");
            return File.Exists(bin) ? bin : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveCandidate(string path)
    {
        if (!File.Exists(path)) return null;
        var ext = Path.GetExtension(path);
        if (OperatingSystem.IsWindows() && (ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || ext.Equals(".ps1", StringComparison.OrdinalIgnoreCase)))
            return FindNpmNativeBinary(Path.GetDirectoryName(path)!);
        return path;
    }

    private static string? FindNpmNativeBinary(string npmBinDir)
    {
        var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        var pkg = $"copilot-win32-{arch}";
        string[] candidates =
        [
            Path.Combine(npmBinDir, "node_modules", "@github", "copilot", "node_modules", "@github", pkg, "copilot.exe"),
            Path.Combine(npmBinDir, "node_modules", "@github", pkg, "copilot.exe"),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    // ── Client lifetime ──────────────────────────────────────────────────────

    private static string WorkDir
    {
        get
        {
            // A neutral working directory, so the CLI doesn't pick up instructions/config from
            // whatever folder the app happens to be launched from.
            var dir = Path.Combine(Path.GetTempPath(), "AITrans-copilot");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private async Task<CopilotClient> GetClientAsync(CancellationToken ct)
    {
        if (!IsSupported)
            throw new PlatformNotSupportedException("The local GitHub Copilot CLI is only available on Windows, macOS and Linux.");

        var cliPath = ResolveCliPath(CliPathOverride)
            ?? throw new FileNotFoundException(
                "GitHub Copilot CLI was not found. Install it with \"npm install -g @github/copilot\" "
                + "(or winget install GitHub.Copilot), run \"copilot login\", or set the CLI path in Settings.");

        await _clientLock.WaitAsync(ct);
        try
        {
            if (_client != null && _clientCliPath == cliPath)
                return _client;

            await ResetClientCoreAsync();

            var client = new CopilotClient(new CopilotClientOptions
            {
                Connection = RuntimeConnection.ForStdio(cliPath),
                WorkingDirectory = WorkDir,
                UseLoggedInUser = true,
            });
            try
            {
                await client.StartAsync(ct);
            }
            catch
            {
                await client.DisposeAsync();
                throw;
            }

            _client = client;
            _clientCliPath = cliPath;
            return client;
        }
        finally
        {
            _clientLock.Release();
        }
    }

    private async Task ResetClientCoreAsync()
    {
        var old = _client;
        _client = null;
        _clientCliPath = null;
        if (old == null) return;
        try { await old.DisposeAsync(); }
        catch { /* the process may already be gone */ }
    }

    /// <summary>Drops the shared CLI process, e.g. after it crashed or the CLI path changed.</summary>
    public async Task ResetAsync()
    {
        await _clientLock.WaitAsync();
        try { await ResetClientCoreAsync(); }
        finally { _clientLock.Release(); }
    }

    public async ValueTask DisposeAsync() => await ResetAsync();

    // ── Public API ───────────────────────────────────────────────────────────

    public async Task<CliStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var client = await GetClientAsync(ct);
        var status = await client.GetStatusAsync(ct);
        var auth = await client.GetAuthStatusAsync(ct);
        return new CliStatus(_clientCliPath ?? "", status.Version ?? "", auth.IsAuthenticated, auth.Login ?? "", auth.StatusMessage ?? "");
    }

    /// <summary>
    /// Lists the models the signed-in Copilot account can currently use, straight from the CLI's
    /// live model catalog (models an organization policy has disabled are left out).
    /// </summary>
    public async Task<List<string>> ListModelsAsync(CancellationToken ct = default)
    {
        var client = await GetClientAsync(ct);
        var auth = await client.GetAuthStatusAsync(ct);
        if (!auth.IsAuthenticated)
            throw new InvalidOperationException(
                $"GitHub Copilot CLI is not signed in. Run \"copilot login\" in a terminal. {auth.StatusMessage}".Trim());

        var models = await client.ListModelsAsync(ct);
        return models
            .Where(m => !string.Equals(m.Policy?.State, "disabled", StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Single-turn completion: <paramref name="systemPrompt"/> replaces Copilot's agent prompt, no tools are exposed.</summary>
    public async Task<string> CompleteAsync(string systemPrompt, string userMessage, string model, CancellationToken ct = default)
    {
        try
        {
            return await CompleteCoreAsync(systemPrompt, userMessage, model, ct);
        }
        catch (IOException)
        {
            // The CLI process died or its pipe broke — start a fresh one and retry once.
            await ResetAsync();
            return await CompleteCoreAsync(systemPrompt, userMessage, model, ct);
        }
    }

    /// <summary>
    /// Multi-turn chat. Copilot sessions can't be seeded with prior assistant turns, so the earlier
    /// conversation is passed as a transcript in front of the new message.
    /// </summary>
    public Task<string> ChatAsync(string systemPrompt, IReadOnlyList<ChatMessage> history, string userMessage, string model, CancellationToken ct = default)
    {
        if (history.Count == 0)
            return CompleteAsync(systemPrompt, userMessage, model, ct);

        var sb = new StringBuilder();
        sb.AppendLine("Conversation so far:");
        foreach (var msg in history)
        {
            sb.Append(msg.Role == ChatRole.User ? "User: " : "Assistant: ");
            sb.AppendLine(msg.Content);
            sb.AppendLine();
        }
        sb.AppendLine("Reply to the user's latest message below, continuing the conversation above.");
        sb.AppendLine();
        sb.Append("User: ").Append(userMessage);
        return CompleteAsync(systemPrompt, sb.ToString(), model, ct);
    }

    private async Task<string> CompleteCoreAsync(string systemPrompt, string userMessage, string model, CancellationToken ct)
    {
        var client = await GetClientAsync(ct);

        // A model saved before switching to the CLI (e.g. the old "gpt-4o" default) may not be in
        // the account's catalog; fall back to the CLI's own default instead of failing the call.
        // ListModelsAsync is cached by the SDK, so this costs one RPC per CLI process.
        string? sessionModel = string.IsNullOrWhiteSpace(model) ? null : model;
        if (sessionModel != null)
        {
            var available = await client.ListModelsAsync(ct);
            if (!available.Any(m => string.Equals(m.Id, sessionModel, StringComparison.OrdinalIgnoreCase)))
            {
                Debug.WriteLine($"[CopilotCli] Model '{sessionModel}' is not available — using the CLI default.");
                sessionModel = null;
            }
        }

        var config = new SessionConfig
        {
            Model = sessionModel,
            WorkingDirectory = WorkDir,
            // Plain chat completion: no file/shell tools, skills, repo instructions or memory.
            AvailableTools = [],
            EnableSkills = false,
            SkipCustomInstructions = true,
            EnableSessionStore = false,
            OnPermissionRequest = PermissionHandler.ApproveAll,
        };
        if (!string.IsNullOrWhiteSpace(systemPrompt))
            config.SystemMessage = new SystemMessageConfig { Mode = SystemMessageMode.Replace, Content = systemPrompt };

        var session = await client.CreateSessionAsync(config, ct);
        var sessionId = session.SessionId;
        try
        {
            AssistantMessageEvent? reply;
            try
            {
                reply = await session.SendAndWaitAsync(new MessageOptions { Prompt = userMessage }, ResponseTimeout, ct);
            }
            catch (OperationCanceledException)
            {
                try { await session.AbortAsync(CancellationToken.None); } catch { /* best effort */ }
                throw;
            }

            return reply?.Data.Content?.Trim()
                ?? throw new InvalidOperationException("GitHub Copilot CLI returned no response.");
        }
        finally
        {
            try { await session.DisposeAsync(); } catch { /* ignore */ }
            try { await client.DeleteSessionAsync(sessionId, CancellationToken.None); } catch { /* ignore */ }
        }
    }
}
