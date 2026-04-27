namespace EventTriage.Api.Services.Options;

/// <summary>
/// Tuning knobs for the triage pipeline, bound from the <c>Triage</c> section
/// in <c>appsettings.json</c>. Defaults are sized for a gpt-4o-mini deployment
/// with a standard quota; adjust for larger deployments or stricter SLAs.
/// </summary>
public sealed class TriageOptions
{
    /// <summary>
    /// Maximum number of LLM calls issued concurrently from a single batch.
    /// Prevents a large batch from saturating Azure OpenAI quota in one burst.
    /// Default: 8.
    /// </summary>
    public int MaxParallelism { get; init; } = 8;

    /// <summary>
    /// Hard timeout applied per event before the resilience pipeline escalates
    /// to a retry or fallback. Should be shorter than the upstream HTTP client
    /// timeout to ensure Polly — not the socket — owns the cancellation.
    /// Default: 12 seconds.
    /// </summary>
    public int PerEventTimeoutSeconds { get; init; } = 12;

    /// <summary>
    /// Number of retry attempts after a transient LLM failure before the
    /// circuit breaker or fallback takes over. Each retry uses exponential
    /// back-off with jitter. Default: 2.
    /// </summary>
    public int MaxRetries { get; init; } = 2;

    /// <summary>
    /// Maximum number of events accepted in a single request. Enforced by
    /// <see cref="Validation.TriageBatchRequestValidator"/>.
    /// Default: 100.
    /// </summary>
    public int MaxBatchSize { get; init; } = 100;
}
