using System;
using System.Collections.Generic;
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

    private static string GetVoice(string language) =>
        VoiceMap.TryGetValue(language, out var voice) ? voice : "en-US-JennyNeural";

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

                var result = await synthesizer.SpeakTextAsync(paragraph);
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
        }
    }

    /// <summary>Stops the currently playing speech, if any.</summary>
    public void Stop()
    {
        var cts = Interlocked.Exchange(ref _activeCts, null);
        cts?.Cancel();
        cts?.Dispose();
    }

    public void Dispose() => Stop();
}

