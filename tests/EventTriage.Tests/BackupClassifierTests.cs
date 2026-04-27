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
    // AuthorizationFailure signals
    [InlineData("""{"code":"403"}""", "AuthorizationFailure")]
    [InlineData("""{"detail":"forbidden resource"}""", "AuthorizationFailure")]
    // DocumentTranslation signals
    [InlineData("""{"msg":"translate document failed"}""", "DocumentTranslation")]
    [InlineData("""{"msg":"transform mapping failed"}""", "DocumentTranslation")]
    [InlineData("""{"doc":"edi 850 processing"}""", "DocumentTranslation")]
    // DataQuality signals
    [InlineData("""{"status":"missing field value"}""", "DataQuality")]
    [InlineData("""{"status":"required field absent"}""", "DataQuality")]
    // InternalSystemError via "internal" keyword
    [InlineData("""{"err":"internal server error"}""", "InternalSystemError")]
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

    [Fact]
    public void Walks_array_elements_for_signal_matching()
    {
        var evt = new ErrorEvent
        {
            EventId = "x",
            Source = "test",
            Payload = JsonDocument.Parse("""{"errors":["connection timeout occurred","retry failed"]}""").RootElement
        };

        var result = _classifier.Classify(evt);
        result.Category.Should().Be("PartnerConnectivity");
        result.Confidence.Should().BeLessOrEqualTo(0.5);
    }

    [Fact]
    public void Walks_numeric_payload_values_for_signal_matching()
    {
        // Numeric "401" should be appended as raw text and match the AuthenticationFailure signal.
        var evt = new ErrorEvent
        {
            EventId = "x",
            Source = "test",
            Payload = JsonDocument.Parse("""{"statusCode":401}""").RootElement
        };

        var result = _classifier.Classify(evt);
        result.Category.Should().Be("AuthenticationFailure");
        result.Confidence.Should().BeLessOrEqualTo(0.5);
    }

    [Fact]
    public void Document_type_contributes_to_signal_matching()
    {
        // The DocumentType field is included in the search string, so "EDI-850" surfaces the "edi" token.
        var evt = new ErrorEvent
        {
            EventId = "x",
            Source = "test",
            DocumentType = "EDI-850",
            Payload = JsonDocument.Parse("""{"msg":"processing failed"}""").RootElement
        };

        var result = _classifier.Classify(evt);
        result.Category.Should().Be("DocumentTranslation");
    }

    [Fact]
    public void NullReference_signal_maps_to_Critical_severity()
    {
        var evt = new ErrorEvent
        {
            EventId = "x",
            Source = "test",
            Payload = JsonDocument.Parse("""{"trace":"NullReferenceException thrown in mapper"}""").RootElement
        };

        var result = _classifier.Classify(evt);
        result.Category.Should().Be("InternalSystemError");
        result.Severity.Should().Be(Severity.Critical);
    }

    [Fact]
    public void Internal_keyword_maps_to_High_severity_not_Critical()
    {
        var evt = new ErrorEvent
        {
            EventId = "x",
            Source = "test",
            Payload = JsonDocument.Parse("""{"err":"internal processing error"}""").RootElement
        };

        var result = _classifier.Classify(evt);
        result.Category.Should().Be("InternalSystemError");
        result.Severity.Should().Be(Severity.High);
    }
}
