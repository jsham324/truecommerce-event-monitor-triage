using System.Text.Json;
using EventTriage.Api.Llm;
using EventTriage.Api.Models;

namespace EventTriage.Api.Resilience;

/// <summary>
/// Pure-code, deterministic classifier used when the LLM call fails (after
/// retries / circuit-breaker open / timeout / schema-violation) OR when the
/// caller explicitly opts out via header.
///
/// Design rules:
///   * No external dependencies - cannot itself fail in a way that needs
///     escalation.
///   * Deliberately conservative confidence (≤ 0.5) so the consuming pipeline
///     routes these into a human-review queue rather than auto-actioning.
///   * Walks the JSON looking for well-known signals. We do NOT try to be
///     exhaustive - just enough to keep the system useful when the LLM is down.
/// </summary>
public sealed class BackupClassifier
{
    /// <summary>
    /// Ordered keyword-to-category mappings. First match wins, so more specific
    /// tokens should appear before broader ones where ambiguity exists.
    /// </summary>
    private static readonly (string Token, string Category, Severity Severity)[] Signals =
    {
        ("schema",        "SchemaValidation",       Severity.Medium),
        ("validation",    "SchemaValidation",       Severity.Medium),
        ("unauthorized",  "AuthenticationFailure",  Severity.High),
        ("401",           "AuthenticationFailure",  Severity.High),
        ("forbidden",     "AuthorizationFailure",   Severity.High),
        ("403",           "AuthorizationFailure",   Severity.High),
        ("timeout",       "PartnerConnectivity",    Severity.High),
        ("timed out",     "PartnerConnectivity",    Severity.High),
        ("connection",    "PartnerConnectivity",    Severity.High),
        ("dns",           "PartnerConnectivity",    Severity.High),
        ("translate",     "DocumentTranslation",    Severity.Medium),
        ("transform",     "DocumentTranslation",    Severity.Medium),
        ("edi",           "DocumentTranslation",    Severity.Medium),
        ("missing",       "DataQuality",            Severity.Medium),
        ("required",      "DataQuality",            Severity.Medium),
        ("duplicate",     "DuplicateSubmission",    Severity.Low),
        ("nullreference", "InternalSystemError",    Severity.Critical),
        ("internal",      "InternalSystemError",    Severity.High)
    };

    /// <summary>
    /// Produces a best-effort classification by scanning the event payload for
    /// well-known signal tokens. Always returns a result; never throws.
    /// </summary>
    /// <param name="evt">The error event to classify.</param>
    /// <returns>
    /// A <see cref="LlmClassification"/> with confidence capped at 0.5, signalling
    /// to downstream consumers that human review is required before any auto-action.
    /// </returns>
    public LlmClassification Classify(ErrorEvent evt)
    {
        var haystack = BuildSearchString(evt).ToLowerInvariant();

        foreach (var (token, category, severity) in Signals)
        {
            if (haystack.Contains(token, StringComparison.Ordinal))
            {
                return new LlmClassification
                {
                    Category = category,
                    Severity = severity,
                    Confidence = 0.45,    // Capped: signals routing to human review
                    Summary = $"Heuristic match on '{token}' from {evt.Source}",
                    RemediationSteps = new[]
                    {
                        "LLM classification was unavailable; this result is heuristic.",
                        "Open the original payload and confirm the category before auto-actioning.",
                        "If the pattern is recurring, consider adding it as a golden test case."
                    },
                    SuggestedOwner = null,
                    PromptTokens = 0,
                    CompletionTokens = 0
                };
            }
        }

        // Truly nothing matched - mark for human review.
        return new LlmClassification
        {
            Category = "Unknown",
            Severity = Severity.Medium,
            Confidence = 0.1,
            Summary = $"Unclassified event from {evt.Source} (LLM unavailable)",
            RemediationSteps = new[]
            {
                "Manual review required: LLM was unavailable and no heuristic matched.",
                "Inspect the raw payload and assign a category."
            },
            SuggestedOwner = null,
            PromptTokens = 0,
            CompletionTokens = 0
        };
    }

    /// <summary>
    /// Flattens the event metadata and every leaf value in the JSON payload into
    /// a single lowercase string for token matching.
    /// </summary>
    /// <param name="evt">The event whose payload will be walked.</param>
    /// <returns>A whitespace-separated string of all searchable tokens.</returns>
    private static string BuildSearchString(ErrorEvent evt)
    {
        // Concatenate all leaf string values from the JSON payload.
        // Cheap, allocation-friendly, and good enough to surface obvious tokens.
        var sb = new System.Text.StringBuilder(capacity: 512);
        sb.Append(evt.Source).Append(' ');
        sb.Append(evt.DocumentType).Append(' ');
        Walk(evt.Payload, sb);
        return sb.ToString();

        static void Walk(JsonElement element, System.Text.StringBuilder sb)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var prop in element.EnumerateObject())
                    {
                        sb.Append(prop.Name).Append(' ');
                        Walk(prop.Value, sb);
                    }
                    break;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray()) Walk(item, sb);
                    break;
                case JsonValueKind.String:
                    sb.Append(element.GetString()).Append(' ');
                    break;
                case JsonValueKind.Number:
                    sb.Append(element.GetRawText()).Append(' ');
                    break;
            }
        }
    }
}
