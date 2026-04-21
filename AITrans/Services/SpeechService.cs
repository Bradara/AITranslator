using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CognitiveServices.Speech;

namespace AITrans.Services;

public sealed class SpeechService : IDisposable
{
    private static readonly Dictionary<string, string> VoiceMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Bulgarian"] = "bg-BG-KalinaNeural",
        ["Russian"] = "ru-RU-DmitryNeural",
        ["English"] = "en-US-AmandaMultilingualNeural",
        ["German"] = "de-DE-ConradNeural",
        ["French"] = "fr-FR-HenriNeural",
        ["Spanish"] = "es-ES-AlvaroNeural",
    };

    // Used only to signal a running session to stop.
    private CancellationTokenSource? _activeCts;
    private readonly object _stateLock = new();
    private TaskCompletionSource<bool>? _resumeTcs;
    private bool _isPaused;

    private static string GetVoice(string language) =>
        VoiceMap.TryGetValue(language, out var voice) ? voice : "en-US-JennyNeural";

    public bool IsPaused => Volatile.Read(ref _isPaused);

    private static string PrepareSpeechText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        // Markdown links: [text](url) -> text
        text = Regex.Replace(text, @"\[(.+?)\]\([^\)]*\)", "$1");
        // HTML links: <a href="...">text</a> -> text
        text = Regex.Replace(text, @"<a\s+[^>]*>(.*?)</a>", "$1",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        // Auto-links: <https://example.com>
        text = Regex.Replace(text, @"<\s*https?://[^>]+>", "");
        // Bare URLs (http/https/www)
        text = Regex.Replace(text, @"\bhttps?://[^\s\)\]>]+", "");
        text = Regex.Replace(text, @"\bwww\.[^\s\)\]>]+", "");

        text = Regex.Replace(text, @"\s{2,}", " ").Trim();
        return text;
    }

    /// <summary>
    /// Speaks each paragraph in sequence, one at a time.
    /// Cancellable via <paramref name="ct"/> or by calling <see cref="Stop"/>.
    /// </summary>
    public async Task SpeakParagraphsAsync(
        IEnumerable<string> paragraphs, string language,
        string apiKey, string region,
        CancellationToken ct = default)
    {
        // Cancel any previously running session and wait for it to end.
        var prev = Interlocked.Exchange(ref _activeCts, null);
        prev?.Cancel();
        prev?.Dispose();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Interlocked.Exchange(ref _activeCts, linked);

        var config = SpeechConfig.FromSubscription(apiKey, region);
        config.SpeechSynthesisVoiceName = GetVoice(language);

        // Create synthesizer inside the method so its lifetime is fully contained here.
        using var synthesizer = new SpeechSynthesizer(config);
        lock (_stateLock)
        {
            _isPaused = false;
            _resumeTcs = null;
        }

        // When cancelled, ask the SDK to stop the current utterance gracefully.
        using var reg = linked.Token.Register(() =>
        {
            try { synthesizer.StopSpeakingAsync().Wait(2000); } catch { /* ignore */ }
        });

        try
        {
            foreach (var paragraph in paragraphs)
            {
                linked.Token.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(paragraph)) continue;

                await WaitIfPausedAsync(linked.Token);

                var speakText = PrepareSpeechText(paragraph);
                if (string.IsNullOrWhiteSpace(speakText)) continue;

                var result = await synthesizer.SpeakTextAsync(speakText);
                linked.Token.ThrowIfCancellationRequested();

                if (result.Reason == ResultReason.Canceled)
                {
                    var details = SpeechSynthesisCancellationDetails.FromResult(result);
                    if (details.Reason == CancellationReason.Error)
                        throw new InvalidOperationException(
                            $"Azure Speech error {details.ErrorCode}: {details.ErrorDetails}");
                    break; // stopped gracefully
                }
            }
        }
        finally
        {
            // Clear the reference only if it still points to our own CTS.
            Interlocked.CompareExchange(ref _activeCts, null, linked);
            TaskCompletionSource<bool>? resumeToRelease = null;
            lock (_stateLock)
            {
                _isPaused = false;
                resumeToRelease = _resumeTcs;
                _resumeTcs = null;
            }
            resumeToRelease?.TrySetResult(true);
        }
    }

    public async Task PauseAsync()
    {
        lock (_stateLock)
        {
            if (_activeCts == null || _isPaused) return;
            _isPaused = true;
            _resumeTcs ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        await Task.CompletedTask;
    }

    public async Task ResumeAsync()
    {
        TaskCompletionSource<bool>? resumeTcs;
        lock (_stateLock)
        {
            if (_activeCts == null || !_isPaused) return;
            _isPaused = false;
            resumeTcs = _resumeTcs;
            _resumeTcs = null;
        }

        resumeTcs?.TrySetResult(true);
        await Task.CompletedTask;
    }

    /// <summary>Stops the currently playing speech, if any.</summary>
    public void Stop()
    {
        var cts = Interlocked.Exchange(ref _activeCts, null);
        cts?.Cancel();
        cts?.Dispose();

        TaskCompletionSource<bool>? resumeToRelease = null;
        lock (_stateLock)
        {
            _isPaused = false;
            resumeToRelease = _resumeTcs;
            _resumeTcs = null;
        }
        resumeToRelease?.TrySetResult(true);
    }

    public void Dispose() => Stop();

    private Task WaitIfPausedAsync(CancellationToken token)
    {
        TaskCompletionSource<bool>? resumeTcs;
        lock (_stateLock)
        {
            resumeTcs = _resumeTcs;
        }
        return resumeTcs == null ? Task.CompletedTask : resumeTcs.Task.WaitAsync(token);
    }
}

