using System.Diagnostics;
using EventTriage.Api.Llm;
using EventTriage.Api.Models;
using EventTriage.Api.Resilience;
using EventTriage.Api.Services.Options;
using Polly;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace EventTriage.Api.Services;

/// <summary>
/// Defines the contract for a batch error event triage service.
/// </summary>
public interface ITriageService
{
    /// <summary>
    /// Classifies a batch of error events and returns structured triage results.
    /// </summary>
    /// <param name="request">The batch request to classify.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<TriageBatchResponse> TriageAsync(
        TriageBatchRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Orchestrates classification of a batch of error events.
///
/// Resilience strategy (per event):
///     timeout → retry → circuit-breaker → fallback heuristic
///
/// Concurrency: events within a batch are classified in parallel up to a
/// configurable degree-of-parallelism so a 100-event batch does not serialize
/// into 100*latency. The cap protects Azure OpenAI quota.
/// </summary>
public sealed class TriageService : ITriageService
{
    private readonly ILlmClassifier _llm;
    private readonly BackupClassifier _fallback;
    private readonly IPromptCatalog _prompts;
    private readonly ResiliencePipeline<LlmClassification> _pipeline;
    private readonly TriageOptions _options;
    private readonly ILogger<TriageService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TriageService"/> class.
    /// </summary>
    /// <param name="llm">The LLM classifier used for primary classification.</param>
    /// <param name="fallback">The fallback classifier used on LLM failures.</param>
    /// <param name="prompts">The prompt catalog used for versioning.</param>
    /// <param name="options">The triage configuration options.</param>
    /// <param name="logger">The logger for diagnostic events.</param>
    public TriageService(
        ILlmClassifier llm,
        BackupClassifier fallback,
        IPromptCatalog prompts,
        TriageOptions options,
        ILogger<TriageService> logger)
    {
        _llm = llm;
        _fallback = fallback;
        _prompts = prompts;
        _options = options;
        _logger = logger;

        _pipeline = new ResiliencePipelineBuilder<LlmClassification>()
            .AddTimeout(TimeSpan.FromSeconds(options.PerEventTimeoutSeconds))
            .AddRetry(new Polly.Retry.RetryStrategyOptions<LlmClassification>
            {
                MaxRetryAttempts = options.MaxRetries,
                BackoffType = DelayBackoffType.Exponential,
                Delay = TimeSpan.FromMilliseconds(200),
                UseJitter = true,
                ShouldHandle = new PredicateBuilder<LlmClassification>()
                    .Handle<HttpRequestException>()
                    .Handle<TimeoutRejectedException>()
                    .Handle<TaskCanceledException>(ex => ex.InnerException is TimeoutException)
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions<LlmClassification>
            {
                FailureRatio = 0.5,
                MinimumThroughput = 10,
                SamplingDuration = TimeSpan.FromSeconds(30),
                BreakDuration = TimeSpan.FromSeconds(15),
                ShouldHandle = new PredicateBuilder<LlmClassification>()
                    .Handle<HttpRequestException>()
                    .Handle<TimeoutRejectedException>()
            })
            .Build();
    }

    /// <summary>
    /// Processes a triage batch request and returns a response containing
    /// classification results and metrics.
    /// </summary>
    /// <param name="request">The batch request containing events to triage.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    public async Task<TriageBatchResponse> TriageAsync(
        TriageBatchRequest request,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var correlationId = Guid.NewGuid().ToString("N");

        var promptVersion = !string.IsNullOrWhiteSpace(request.PromptVersion)
            ? request.PromptVersion
            : _prompts.DefaultVersion;

        // SemaphoreSlim governs how many in-flight LLM calls we issue from a
        // single batch. Right-sized for our quota; tunable via config.
        using var concurrencyGate = new SemaphoreSlim(_options.MaxParallelism);

        var tasks = request.Events
            .Select(evt => ClassifyOneAsync(evt, promptVersion, concurrencyGate, cancellationToken))
            .ToArray();

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        stopwatch.Stop();

        var metrics = new TriageMetrics
        {
            TotalEvents = results.Length,
            ClassifiedByLlm = results.Count(r => r.Source == "llm"),
            ClassifiedByFallback = results.Count(r => r.Source == "fallback-heuristic"),
            DeadLettered = results.Count(r => r.Source == "dead-letter"),
            ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
            PromptTokens = results.Where(r => r.Source == "llm")
                                  .Select(r => r.PromptTokens ?? 0).DefaultIfEmpty(0).Sum(),
            CompletionTokens = results.Where(r => r.Source == "llm")
                                  .Select(r => r.CompletionTokens ?? 0).DefaultIfEmpty(0).Sum()
        };

        _logger.LogInformation(
            "Triage batch {CorrelationId}: total={Total} llm={Llm} fallback={Fallback} dead={Dead} elapsedMs={Elapsed}",
            correlationId, metrics.TotalEvents, metrics.ClassifiedByLlm,
            metrics.ClassifiedByFallback, metrics.DeadLettered, metrics.ElapsedMilliseconds);

        return new TriageBatchResponse
        {
            CorrelationId = correlationId,
            Results = results.Select(StripTokenCounts).ToArray(),
            Metrics = metrics
        };
    }

    /// <summary>
    /// Classifies a single error event using the LLM pipeline or fallback path.
    /// </summary>
    /// <param name="evt">The event to classify.</param>
    /// <param name="promptVersion">The prompt version to use for classification.</param>
    /// <param name="gate">A concurrency gate to limit in-flight LLM calls.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>An internal classification result carrying token metrics.</returns>
    private async Task<InternalResult> ClassifyOneAsync(
        ErrorEvent evt,
        string promptVersion,
        SemaphoreSlim gate,
        CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            try
            {
                var llmResult = await _pipeline.ExecuteAsync(
                    async token => await _llm.ClassifyAsync(evt, promptVersion, token).ConfigureAwait(false),
                    ct).ConfigureAwait(false);

                return InternalResult.FromLlm(evt.EventId, llmResult, promptVersion);
            }
            catch (BrokenCircuitException)
            {
                _logger.LogWarning("Circuit breaker open for event {EventId}; falling back", evt.EventId);
                return Fallback(evt);
            }
            catch (TimeoutRejectedException)
            {
                _logger.LogWarning("LLM timeout for event {EventId}; falling back", evt.EventId);
                return Fallback(evt);
            }
            catch (LlmContractException ex)
            {
                _logger.LogWarning(ex, "LLM contract violation for event {EventId}; falling back", evt.EventId);
                return Fallback(evt);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "LLM transport failure for event {EventId}; falling back", evt.EventId);
                return Fallback(evt);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Unexpected failure classifying event {EventId}", evt.EventId);
                return InternalResult.DeadLetter(evt.EventId, ex.Message);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Runs the heuristic fallback classifier for an event.
    /// </summary>
    /// <param name="evt">The event to classify with the fallback.</param>
    /// <returns>An internal result produced by the fallback classifier.</returns>
    private InternalResult Fallback(ErrorEvent evt)
    {
        var heuristic = _fallback.Classify(evt);
        return InternalResult.FromFallback(evt.EventId, heuristic);
    }

    /// <summary>
    /// Converts an internal result into the public-facing triage result.
    /// </summary>
    /// <param name="internalResult">The internal result to convert.</param>
    /// <returns>A <see cref="TriageResult"/> without token metrics.</returns>
    private static TriageResult StripTokenCounts(InternalResult internalResult)
        => new()
        {
            EventId = internalResult.EventId,
            Category = internalResult.Category,
            Severity = internalResult.Severity,
            Confidence = internalResult.Confidence,
            Summary = internalResult.Summary,
            RemediationSteps = internalResult.RemediationSteps,
            SuggestedOwner = internalResult.SuggestedOwner,
            Source = internalResult.Source,
            PromptVersion = internalResult.PromptVersion
        };

    /// <summary>
    /// Internal carrier that keeps token counts on each item until aggregation.
    /// </summary>
    private sealed record InternalResult
    {
        /// <summary>
        /// The identifier of the original event.
        /// </summary>
        public required string EventId { get; init; }

        /// <summary>
        /// The category assigned to the event.
        /// </summary>
        public required string Category { get; init; }

        /// <summary>
        /// The severity assigned to the event.
        /// </summary>
        public required Severity Severity { get; init; }

        /// <summary>
        /// The classifier's confidence score.
        /// </summary>
        public required double Confidence { get; init; }

        /// <summary>
        /// A brief summary of the issue.
        /// </summary>
        public required string Summary { get; init; }

        /// <summary>
        /// The remediation steps produced by the classifier.
        /// </summary>
        public required IReadOnlyList<string> RemediationSteps { get; init; }

        /// <summary>
        /// Optional suggested owning team or queue.
        /// </summary>
        public string? SuggestedOwner { get; init; }

        /// <summary>
        /// The source of the classification result.
        /// </summary>
        public required string Source { get; init; }

        /// <summary>
        /// The prompt version that was used for LLM classification.
        /// </summary>
        public string? PromptVersion { get; init; }

        /// <summary>
        /// Tokens consumed by the prompt in the LLM call.
        /// </summary>
        public int? PromptTokens { get; init; }

        /// <summary>
        /// Tokens consumed by the completion in the LLM call.
        /// </summary>
        public int? CompletionTokens { get; init; }

        /// <summary>
        /// Creates an internal result from an LLM classification.
        /// </summary>
        public static InternalResult FromLlm(string eventId, LlmClassification c, string version)
            => new()
            {
                EventId = eventId,
                Category = c.Category,
                Severity = c.Severity,
                Confidence = c.Confidence,
                Summary = c.Summary,
                RemediationSteps = c.RemediationSteps,
                SuggestedOwner = c.SuggestedOwner,
                Source = "llm",
                PromptVersion = version,
                PromptTokens = c.PromptTokens,
                CompletionTokens = c.CompletionTokens
            };

        /// <summary>
        /// Creates an internal result from a fallback classification.
        /// </summary>
        public static InternalResult FromFallback(string eventId, LlmClassification c)
            => new()
            {
                EventId = eventId,
                Category = c.Category,
                Severity = c.Severity,
                Confidence = c.Confidence,
                Summary = c.Summary,
                RemediationSteps = c.RemediationSteps,
                SuggestedOwner = c.SuggestedOwner,
                Source = "fallback-heuristic",
                PromptVersion = null
            };

        /// <summary>
        /// Creates a dead-letter internal result when both classification paths fail.
        /// </summary>
        public static InternalResult DeadLetter(string eventId, string reason)
            => new()
            {
                EventId = eventId,
                Category = "Unknown",
                Severity = Severity.High,
                Confidence = 0,
                Summary = $"Triage failed: {reason}",
                RemediationSteps = new[] { "Manual investigation required - both LLM and fallback paths failed." },
                SuggestedOwner = null,
                Source = "dead-letter",
                PromptVersion = null
            };
    }
}
