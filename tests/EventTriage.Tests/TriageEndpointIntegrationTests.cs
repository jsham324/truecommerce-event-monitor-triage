using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EventTriage.Api.Llm;
using EventTriage.Api.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace EventTriage.Tests;

/// <summary>
/// End-to-end tests through the real ASP.NET Core pipeline. The LLM is stubbed
/// at the ILlmClassifier boundary so we exercise routing, validation,
/// resilience, serialisation, and HTTP semantics without burning real tokens.
/// </summary>
public class TriageEndpointIntegrationTests : IClassFixture<TriageWebAppFactory>
{
    private readonly TriageWebAppFactory _factory;

    public TriageEndpointIntegrationTests(TriageWebAppFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Posting_a_valid_batch_returns_200_with_one_result_per_event()
    {
        _factory.LlmClassifier.ClassifyAsync(
                Arg.Any<ErrorEvent>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new LlmClassification
            {
                Category = "SchemaValidation",
                Severity = Severity.Medium,
                Confidence = 0.85,
                Summary = "ok",
                RemediationSteps = new[] { "step" }
            });

        var client = _factory.CreateClient();

        var request = new
        {
            events = new[]
            {
                new
                {
                    eventId = "evt-1",
                    source = "edi-gateway",
                    occurredAt = DateTimeOffset.UtcNow,
                    payload = JsonDocument.Parse("""{"msg":"test"}""").RootElement
                }
            }
        };

        var response = await client.PostAsJsonAsync("/api/v1/triage", request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<TriageBatchResponse>();
        body!.Results.Should().HaveCount(1);
        body.Results[0].Source.Should().Be("llm");
    }

    [Fact]
    public async Task Empty_event_list_returns_400_problem_details()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/triage", new { events = Array.Empty<object>() });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Sample_endpoint_returns_demo_payload()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/triage/sample");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Unknown_prompt_version_returns_400_not_dead_letter()
    {
        var client = _factory.CreateClient();

        var request = new
        {
            promptVersion = "v99-does-not-exist",
            events = new[]
            {
                new
                {
                    eventId = "evt-1",
                    source = "edi-gateway",
                    occurredAt = DateTimeOffset.UtcNow,
                    payload = JsonDocument.Parse("""{"msg":"test"}""").RootElement
                }
            }
        };

        var response = await client.PostAsJsonAsync("/api/v1/triage", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Health_endpoints_are_exposed()
    {
        var client = _factory.CreateClient();
        (await client.GetAsync("/health/live")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/health/ready")).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

public sealed class TriageWebAppFactory : WebApplicationFactory<Program>
{
    public ILlmClassifier LlmClassifier { get; } = Substitute.For<ILlmClassifier>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Ensure the test config has whatever the LLM options validator needs.
        builder.UseSetting("Llm:Endpoint", "https://test.invalid/");
        builder.UseSetting("Llm:Deployment", "test-deployment");
        builder.UseSetting("Llm:DefaultPromptVersion", "v1");

        builder.ConfigureServices(services =>
        {
            // Replace the real LLM classifier with the substitute.
            var existing = services.Single(d => d.ServiceType == typeof(ILlmClassifier));
            services.Remove(existing);
            services.AddSingleton(LlmClassifier);
        });
    }
}
