using System.Text.Json.Serialization;

namespace EventTriage.Api.Models;

/// <summary>
/// Severity score returned by the triage pipeline.
/// Mirrors a typical SRE-style severity scale; numeric values are stable
/// for downstream alerting / dashboards.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Severity
{
    /// <summary>Not an error; mis-routed log line or expected noise.</summary>
    Informational = 0,
    /// <summary>Cosmetic or non-blocking; no partner impact.</summary>
    Low = 1,
    /// <summary>Degraded but partner is still functional.</summary>
    Medium = 2,
    /// <summary>A single partner's flow is blocked.</summary>
    High = 3,
    /// <summary>Revenue-impacting, partner fully blocked, or data loss possible.</summary>
    Critical = 4
}

/// <summary>
/// Inbound batch from the ingestion pipeline. Capped on the validator side.
/// </summary>
public sealed record TriageBatchRequest
{
    /// <summary>
    /// The events to classify. Must contain at least one item and no more than
    /// the configured <c>Triage:MaxBatchSize</c>.
    /// </summary>
    public required IReadOnlyList<ErrorEvent> Events { get; init; }

    /// <summary>
    /// Optional override of the active prompt version, useful for shadow / A-B testing.
    /// If null the default from configuration is used.
    /// </summary>
    public string? PromptVersion { get; init; }
}

/// <summary>
/// One classified item in the response.
/// </summary>
public sealed record TriageResult
{
    /// <summary>Echoes the <see cref="ErrorEvent.EventId"/> for correlation.</summary>
    public required string EventId { get; init; }

    /// <summary>
    /// Error category assigned by the classifier (e.g. <c>SchemaValidation</c>,
    /// <c>PartnerConnectivity</c>). Matches the enum defined in the prompt schema.
    /// </summary>
    public required string Category { get; init; }

    /// <summary>Severity level assigned by the classifier.</summary>
    public required Severity Severity { get; init; }

    /// <summary>
    /// 0..1 confidence reported by the classifier. The fallback heuristic
    /// always reports ≤ 0.5 so consumers can route low-confidence items
    /// to a human queue.
    /// </summary>
    public required double Confidence { get; init; }

    /// <summary>
    /// One-line human readable summary suitable for an alert.
    /// </summary>
    public required string Summary { get; init; }

    /// <summary>
    /// Suggested remediation steps, ordered, each ≤ 200 chars. Validated
    /// against the JSON schema returned by the LLM.
    /// </summary>
    public required IReadOnlyList<string> RemediationSteps { get; init; }

    /// <summary>
    /// Suggested owning team / queue if one can be inferred.
    /// </summary>
    public string? SuggestedOwner { get; init; }

    /// <summary>
    /// Which path produced this result: "llm", "fallback-heuristic",
    /// or "dead-letter". Lets dashboards monitor degradation.
    /// </summary>
    public required string Source { get; init; }

    /// <summary>
    /// Prompt version used. Empty when the fallback path was taken.
    /// </summary>
    public string? PromptVersion { get; init; }
}

/// <summary>
/// Final response envelope. Keeps batch-level metadata for observability
/// without polluting each item.
/// </summary>
public sealed record TriageBatchResponse
{
    /// <summary>Batch-scoped correlation ID for end-to-end tracing.</summary>
    public required string CorrelationId { get; init; }

    /// <summary>One result per event in the request, in submission order.</summary>
    public required IReadOnlyList<TriageResult> Results { get; init; }

    /// <summary>Aggregate counters and timing for the batch.</summary>
    public required TriageMetrics Metrics { get; init; }
}

/// <summary>
/// Aggregate counters for a triage batch. Use these to monitor pipeline health:
/// a rising <see cref="ClassifiedByFallback"/> ratio is an early signal that the
/// LLM is degraded before any customer-facing alarm fires.
/// </summary>
public sealed record TriageMetrics
{
    /// <summary>Total number of events submitted in the batch.</summary>
    public required int TotalEvents { get; init; }

    /// <summary>Events successfully classified by the primary LLM path.</summary>
    public required int ClassifiedByLlm { get; init; }

    /// <summary>Events classified by the heuristic fallback (LLM unavailable or contract violation).</summary>
    public required int ClassifiedByFallback { get; init; }

    /// <summary>Events that could not be classified by either path and require manual review.</summary>
    public required int DeadLettered { get; init; }

    /// <summary>Wall-clock time for the entire batch, including parallelism overhead.</summary>
    public required long ElapsedMilliseconds { get; init; }

    /// <summary>Total prompt tokens consumed across all successful LLM calls in this batch.</summary>
    public int? PromptTokens { get; init; }

    /// <summary>Total completion tokens consumed across all successful LLM calls in this batch.</summary>
    public int? CompletionTokens { get; init; }
}
