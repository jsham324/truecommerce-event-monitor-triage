using System.Text.Json;
using EventTriage.Api.Models;
using EventTriage.Api.Resilience;
using FluentAssertions;
using Xunit;

namespace EventTriage.Tests;

public class BackupClassifierTests
{
    private readonly BackupClassifier _classifier = new();

    [Theory]
    [InlineData("""{"message":"X12 schema validation failed"}""", "SchemaValidation")]
    [InlineData("""{"reason":"401 Unauthorized partner cert"}""", "AuthenticationFailure")]
    [InlineData("""{"detail":"connection timed out after 30s"}""", "PartnerConnectivity")]
    [InlineData("""{"err":"DNS lookup failed"}""", "PartnerConnectivity")]
    [InlineData("""{"log":"NullReferenceException in mapper"}""", "InternalSystemError")]
    [InlineData("""{"info":"Duplicate 997 received"}""", "DuplicateSubmission")]
    public void Recognises_well_known_signals(string payload, string expectedCategory)
    {
        var evt = new ErrorEvent
        {
            EventId = "x",
            Source = "test",
            Payload = JsonDocument.Parse(payload).RootElement
        };

        var result = _classifier.Classify(evt);
        result.Category.Should().Be(expectedCategory);
        result.Confidence.Should().BeLessOrEqualTo(0.5,
            "the heuristic must always cap confidence so consumers route to human review");
    }

    [Fact]
    public void Falls_through_to_unknown_when_no_signal_matches()
    {
        var evt = new ErrorEvent
        {
            EventId = "x",
            Source = "test",
            Payload = JsonDocument.Parse("""{"completelyOpaqueKey":"completely opaque value"}""").RootElement
        };

        var result = _classifier.Classify(evt);
        result.Category.Should().Be("Unknown");
        result.Confidence.Should().BeLessThan(0.2);
    }
}
