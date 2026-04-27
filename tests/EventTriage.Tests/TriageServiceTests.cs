using System.Text.Json;
using EventTriage.Api.Llm;
using EventTriage.Api.Models;
using EventTriage.Api.Resilience;
using EventTriage.Api.Services;
using EventTriage.Api.Services.Options;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace EventTriage.Tests;

public class TriageServiceTests
{
    private static ErrorEvent NewEvent(string id = "evt-1", string source = "edi-gateway-na",
        string payloadJson = """{"error_code":"VALIDATION","message":"Required element missing"}""")
    {
        return new ErrorEvent
        {
            EventId = id,
            Source = source,
            Payload = JsonDocument.Parse(payloadJson).RootElement
        };
    }

    private static TriageService BuildService(
        ILlmClassifier llm,
        TriageOptions? options = null)
    {
        return new TriageService(
            llm: llm,
            fallback: new BackupClassifier(),
            prompts: new InMemoryPromptCatalog("v1"),
            options: options ?? new TriageOptions
            {
                MaxParallelism = 4,
                PerEventTimeoutSeconds = 5,
                MaxRetries = 1,
                MaxBatchSize = 100
            },
            logger: NullLogger<TriageService>.Instance);
    }

    [Fact]
    public async Task Happy_path_returns_llm_classification_with_prompt_version()
    {
        var llm = Substitute.For<ILlmClassifier>();
        llm.ClassifyAsync(Arg.Any<ErrorEvent>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new LlmClassification
            {
                Category = "SchemaValidation",
                Severity = Severity.Medium,
                Confidence = 0.9,
                Summary = "EDI 850 missing element",
                RemediationSteps = new[] { "Re-emit with element 03" },
                SuggestedOwner = "EDI-Ops",
                PromptTokens = 100,
                CompletionTokens = 50
            });

        var service = BuildService(llm);

        var response = await service.TriageAsync(
            new TriageBatchRequest { Events = new[] { NewEvent() } },
            CancellationToken.None);

        response.Results.Should().HaveCount(1);
        var result = response.Results[0];
        result.Source.Should().Be("llm");
        result.PromptVersion.Should().Be("v1");
        result.Category.Should().Be("SchemaValidation");
        result.Confidence.Should().BeApproximately(0.9, 1e-9);
        response.Metrics.ClassifiedByLlm.Should().Be(1);
        response.Metrics.ClassifiedByFallback.Should().Be(0);
    }

    [Fact]
    public async Task Falls_back_to_heuristic_when_llm_throws_transport_error()
    {
        var llm = Substitute.For<ILlmClassifier>();
        llm.ClassifyAsync(Arg.Any<ErrorEvent>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<LlmClassification>>(_ => throw new HttpRequestException("simulated"));

        var service = BuildService(llm);

        var response = await service.TriageAsync(
            new TriageBatchRequest
            {
                Events = new[] { NewEvent(payloadJson: """{"detail":"connection timeout to partner"}""") }
            },
            CancellationToken.None);

        response.Results.Should().HaveCount(1);
        var result = response.Results[0];
        result.Source.Should().Be("fallback-heuristic");
        result.Confidence.Should().BeLessOrEqualTo(0.5,
            "fallback must be conservative so consumers route to human review");
        result.Category.Should().Be("PartnerConnectivity");
        response.Metrics.ClassifiedByFallback.Should().Be(1);
    }

    [Fact]
    public async Task Falls_back_when_llm_returns_invalid_schema()
    {
        var llm = Substitute.For<ILlmClassifier>();
        llm.ClassifyAsync(Arg.Any<ErrorEvent>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<LlmClassification>>(_ =>
                throw new LlmContractException("schema mismatch", new JsonException("bad")));

        var service = BuildService(llm);

        var response = await service.TriageAsync(
            new TriageBatchRequest { Events = new[] { NewEvent() } },
            CancellationToken.None);

        response.Results[0].Source.Should().Be("fallback-heuristic");
    }

    [Fact]
    public async Task Retries_on_transient_failure_then_succeeds()
    {
        var llm = Substitute.For<ILlmClassifier>();
        var callCount = 0;
        llm.ClassifyAsync(Arg.Any<ErrorEvent>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                if (callCount == 1)
                    throw new HttpRequestException("transient");
                return Task.FromResult(new LlmClassification
                {
                    Category = "PartnerConnectivity",
                    Severity = Severity.High,
                    Confidence = 0.8,
                    Summary = "ok after retry",
                    RemediationSteps = new[] { "step" }
                });
            });

        var service = BuildService(llm, new TriageOptions
        {
            MaxParallelism = 2,
            PerEventTimeoutSeconds = 5,
            MaxRetries = 2,
            MaxBatchSize = 100
        });

        var response = await service.TriageAsync(
            new TriageBatchRequest { Events = new[] { NewEvent() } },
            CancellationToken.None);

        callCount.Should().Be(2);
        response.Results[0].Source.Should().Be("llm");
    }

    [Fact]
    public async Task Mixed_batch_aggregates_metrics_correctly()
    {
        var llm = Substitute.For<ILlmClassifier>();
        llm.ClassifyAsync(Arg.Is<ErrorEvent>(e => e.EventId == "good"),
                          Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new LlmClassification
            {
                Category = "DataQuality",
                Severity = Severity.Low,
                Confidence = 0.7,
                Summary = "ok",
                RemediationSteps = new[] { "step" },
                PromptTokens = 80,
                CompletionTokens = 30
            });
        llm.ClassifyAsync(Arg.Is<ErrorEvent>(e => e.EventId == "bad"),
                          Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<LlmClassification>>(_ => throw new HttpRequestException("boom"));

        var service = BuildService(llm);

        var response = await service.TriageAsync(
            new TriageBatchRequest
            {
                Events = new[]
                {
                    NewEvent(id: "good"),
                    NewEvent(id: "bad", payloadJson: """{"msg":"timeout"}""")
                }
            },
            CancellationToken.None);

        response.Metrics.TotalEvents.Should().Be(2);
        response.Metrics.ClassifiedByLlm.Should().Be(1);
        response.Metrics.ClassifiedByFallback.Should().Be(1);
        response.Metrics.PromptTokens.Should().Be(80);
        response.Metrics.CompletionTokens.Should().Be(30);
    }

    [Fact]
    public async Task Honours_caller_supplied_prompt_version_override()
    {
        var llm = Substitute.For<ILlmClassifier>();
        llm.ClassifyAsync(Arg.Any<ErrorEvent>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new LlmClassification
            {
                Category = "Unknown",
                Severity = Severity.Low,
                Confidence = 0.5,
                Summary = "x",
                RemediationSteps = new[] { "y" }
            });

        var service = BuildService(llm);

        await service.TriageAsync(
            new TriageBatchRequest
            {
                Events = new[] { NewEvent() },
                PromptVersion = "v2-experimental"
            },
            CancellationToken.None);

        await llm.Received(1).ClassifyAsync(
            Arg.Any<ErrorEvent>(),
            "v2-experimental",
            Arg.Any<CancellationToken>());
    }
}
