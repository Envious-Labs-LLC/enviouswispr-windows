using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace EnviousWispr.Polish;

/// Localhost OpenAI-wire polisher for EG-1. Contract ported from
/// EGOneConnector.swift: temperature 0, explicit output-token cap, and every
/// failure maps to the SILENT bypass — a local-server hiccup reads as "no
/// polish this time" (raw transcript is used), never as an error state.
public sealed class EgOnePolisher
{
    private readonly HttpClient _http;
    private readonly EgOneEndpoint _endpoint;
    private readonly int _requestTimeoutSeconds;

    public EgOnePolisher(EgOneEndpoint endpoint, int requestTimeoutSeconds)
    {
        _endpoint = endpoint;
        _requestTimeoutSeconds = requestTimeoutSeconds;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(requestTimeoutSeconds) };
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);
    }

    /// Returns the polished text, or null when polish must be skipped
    /// (server down, truncated output, empty/malformed response).
    public async Task<string?> PolishAsync(string rawTranscript, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawTranscript)) return null;

        // Production cap (LLMPolishStep.swift, case .egOne): character-count,
        // CJK-safe, tight 256 floor for a fixed-prompt instruct tune.
        var maxTokens = Math.Max(rawTranscript.Length, 256);
        var body = JsonSerializer.Serialize(new
        {
            model = "eg-1",
            messages = new[]
            {
                new { role = "system", content = EgOnePrompt.SystemPrompt },
                new { role = "user", content = EgOnePrompt.BuildUserMessage(rawTranscript) },
            },
            max_tokens = maxTokens,
            temperature = 0,
        });

        // One retry on connection refused/reset (covers the restart-once
        // window after a server crash) — the explicit retry decision.
        for (var attempt = 0; attempt <= 1; attempt++)
        {
            if (attempt > 0) await Task.Delay(750, ct);
            try
            {
                var resp = await _http.PostAsync(_endpoint.Url,
                    new StringContent(body, Encoding.UTF8, "application/json"), ct);
                if (!resp.IsSuccessStatusCode) return null;

                var data = await resp.Content.ReadAsStringAsync(ct);
                return ParseSuccess(data);
            }
            catch (HttpRequestException) { continue; }
            catch (TaskCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { return null; } // timeout etc. → silent bypass
        }
        return null;
    }

    /// Ported from EGOneConnector.parseSuccess.
    internal static string? ParseSuccess(string data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (!root.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0) return null;
            var first = choices[0];
            if (first.TryGetProperty("finish_reason", out var fr) &&
                fr.ValueKind == JsonValueKind.String &&
                fr.GetString() == "length")
            {
                // Generation stopped at the max_tokens cap — content is a
                // partial rewrite; accepting it pastes truncated polish.
                return null;
            }
            if (!first.TryGetProperty("message", out var msg) ||
                !msg.TryGetProperty("content", out var contentEl) ||
                contentEl.ValueKind != JsonValueKind.String) return null;

            var content = contentEl.GetString() ?? "";
            var cleaned = CleanPolishedText(content);
            return cleaned.Length > 0 ? cleaned : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// Trim + strip echoed <TRANSCRIPT> sandwich tags (the model can echo the
    /// wrapper) + drop an echoed system-preamble line.
    internal static string CleanPolishedText(string content)
    {
        var s = content.Trim();
        foreach (var open in new[] { "<TRANSCRIPT>", "<transcript>", "<\u200CTRANSCRIPT>", "<\u200Ctranscript>" })
        {
            if (s.StartsWith(open, StringComparison.Ordinal)) s = s[open.Length..].TrimStart();
        }
        foreach (var close in new[] { "</TRANSCRIPT>", "</transcript>", "<\u200C/TRANSCRIPT>", "<\u200C/transcript>" })
        {
            if (s.EndsWith(close, StringComparison.Ordinal)) s = s[..^close.Length].TrimEnd();
        }
        return s.Trim();
    }
}

/// The Mac's activation probe: GREEN requires the full transformation, not
/// merely HTTP 200 (EGOneServerManager.probeHealth).
public static class EgOneProbe
{
    public const string ProbeTranscript = "so um move the meeting to thursday no wait friday";

    public static (bool Green, string Output) Evaluate(string? polished)
    {
        if (polished is null) return (false, "<skipped>");
        var o = polished.ToLowerInvariant();
        var green =
            o.Contains("friday") &&
            !System.Text.RegularExpressions.Regex.IsMatch(o, @"\bum\b") &&
            !o.Contains("thursday") &&
            !o.Contains("no wait");
        return (green, polished);
    }
}
