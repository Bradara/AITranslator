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
        ["Bulgarian"] = "bg-BG-BorislavNeural",
        ["Russian"] = "ru-RU-DmitryNeural",
        ["English"] = "en-US-JennyNeural",
        ["German"] = "de-DE-ConradNeural",
        ["French"] = "fr-FR-HenriNeural",
        ["Spanish"] = "es-ES-AlvaroNeural",
    };

    private SpeechSynthesizer? _synthesizer;

    private static string GetVoice(string language) =>
        VoiceMap.TryGetValue(language, out var voice) ? voice : "en-US-JennyNeural";

    /// <summary>
    /// Speaks each paragraph in sequence, one at a time.
    /// Cancellable between (or during) paragraphs.
    /// </summary>
    public async Task SpeakParagraphsAsync(
        IEnumerable<string> paragraphs, string language,
        string apiKey, string region,
        CancellationToken ct = default)
    {
        Stop(); // stop any running session

        var config = SpeechConfig.FromSubscription(apiKey, region);
        config.SpeechSynthesisVoiceName = GetVoice(language);
        _synthesizer = new SpeechSynthesizer(config);

        // wire cancellation to stop the synthesizer
        using var reg = ct.Register(() => _synthesizer?.StopSpeakingAsync());

        foreach (var paragraph in paragraphs)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(paragraph)) continue;

            var result = await _synthesizer.SpeakTextAsync(paragraph);
            ct.ThrowIfCancellationRequested();

            if (result.Reason == ResultReason.Canceled)
            {
                var details = SpeechSynthesisCancellationDetails.FromResult(result);
                if (details.Reason == CancellationReason.Error)
                    throw new InvalidOperationException(
                        $"Azure Speech error {details.ErrorCode}: {details.ErrorDetails}");
                break; // stopped externally (not an error)
            }
        }
    }

    public void Stop()
    {
        if (_synthesizer != null)
        {
            try { _synthesizer.StopSpeakingAsync().Wait(1000); } catch { /* ignore */ }
            _synthesizer.Dispose();
            _synthesizer = null;
        }
    }

    public void Dispose() => Stop();
}
