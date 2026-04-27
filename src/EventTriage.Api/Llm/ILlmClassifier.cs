using EventTriage.Api.Models;

namespace EventTriage.Api.Llm;

/// <summary>
/// Abstraction over whatever model provider we are using. The endpoint
/// depends on this interface, never on Azure.AI.OpenAI directly, so we can:
///   * swap providers (Azure OpenAI -> Bedrock -> OSS via Ollama),
///   * stub it cleanly in unit tests,
///   * shadow-test prompts side by side.
/// </summary>
public interface ILlmClassifier
{
    /// <summary>
    /// Classify a single error event. Throws on transport failure — resilience
    /// (retry, circuit breaker, fallback) is layered on top by the caller.
    /// </summary>
    /// <param name="evt">The error event to classify.</param>
    /// <param name="promptVersion">
    /// Version key used to look up the system prompt and response schema from
    /// <see cref="IPromptCatalog"/>. Must be a registered version.
    /// </param>
    /// <param name="cancellationToken">Propagated from the caller's resilience pipeline.</param>
    /// <returns>A validated classification produced by the model.</returns>
    Task<LlmClassification> ClassifyAsync(
        ErrorEvent evt,
        string promptVersion,
        CancellationToken cancellationToken);
}

/// <summary>
/// Raw classification returned by the model after schema validation.
/// </summary>
/// <summary>
/// Raw classification returned by the model after schema validation.
/// Consumed by <see cref="Services.TriageService"/> to build
/// the public <see cref="TriageResult"/>.
/// </summary>
public sealed record LlmClassification
{
    /// <summary>Error category assigned by the model (e.g. <c>SchemaValidation</c>, <c>PartnerConnectivity</c>).</summary>
    public required string Category { get; init; }

    /// <summary>Severity level assigned by the model.</summary>
    public required Severity Severity { get; init; }

    /// <summary>
    /// Model's self-reported confidence in the classification, clamped to 0..1.
    /// The <see cref="EventTriage.Api.Resilience.BackupClassifier"/> always returns ≤ 0.5.
    /// </summary>
    public required double Confidence { get; init; }

    /// <summary>One-line human-readable summary of the error (≤ 140 chars).</summary>
    public required string Summary { get; init; }

    /// <summary>Ordered remediation steps (1–5 items, each ≤ 200 chars).</summary>
    public required IReadOnlyList<string> RemediationSteps { get; init; }

    /// <summary>Suggested owning team or queue, if the model could infer one.</summary>
    public string? SuggestedOwner { get; init; }

    /// <summary>Input tokens consumed by this call; zero when produced by the fallback.</summary>
    public int PromptTokens { get; init; }

    /// <summary>Output tokens consumed by this call; zero when produced by the fallback.</summary>
    public int CompletionTokens { get; init; }
}
