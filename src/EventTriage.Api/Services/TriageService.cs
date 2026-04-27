using System.Diagnostics;
using EventTriage.Api.Llm;
using EventTriage.Api.Models;
using EventTriage.Api.Resilience;
using EventTriage.Api.Services.Options;
using Polly;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace EventTriage.Api.Services;

public interface ITriageService
{
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

    // Internal version carries token counts so we can aggregate them; the
    // public TriageResult does not need per-item token data.
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

    private InternalResult Fallback(ErrorEvent evt)
    {
        var heuristic = _fallback.Classify(evt);
        return InternalResult.FromFallback(evt.EventId, heuristic);
    }

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

    // Internal carrier - keeps token counts on each item until aggregation.
    private sealed record InternalResult
    {
        public required string EventId { get; init; }
        public required string Category { get; init; }
        public required Severity Severity { get; init; }
        public required double Confidence { get; init; }
        public required string Summary { get; init; }
        public required IReadOnlyList<string> RemediationSteps { get; init; }
        public string? SuggestedOwner { get; init; }
        public required string Source { get; init; }
        public string? PromptVersion { get; init; }
        public int? PromptTokens { get; init; }
        public int? CompletionTokens { get; init; }

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
