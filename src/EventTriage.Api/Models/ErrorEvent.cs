using System.Text.Json;
using System.Text.Json.Serialization;

namespace EventTriage.Api.Models;

/// <summary>
/// A single inbound error event from any source system.
/// Schema is intentionally permissive: source systems use different field names
/// and the payload is a free-form JSON object that we extract context from.
/// </summary>
public sealed record ErrorEvent
{
    /// <summary>
    /// Idempotency key. If the source system does not provide one we generate it.
    /// Used for deduplication and end-to-end correlation through the pipeline.
    /// </summary>
    public required string EventId { get; init; }

    /// <summary>
    /// Logical name of the originating system (e.g. "edi-gateway-na",
    /// "scheduler-uk", "legacy-acme-onboarding"). Drives routing and ownership.
    /// </summary>
    public required string Source { get; init; }

    /// <summary>
    /// When the upstream system observed the error. May lag ingestion time.
    /// </summary>
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Free-form payload. Captured as raw JSON because every acquired platform
    /// emits a different shape. Normalisation happens in the pipeline upstream
    /// of this service; this endpoint deals with whatever survives.
    /// </summary>
    [JsonPropertyName("payload")]
    public JsonElement Payload { get; init; }

    /// <summary>
    /// Optional partner / customer identifier when known. Used to weight severity.
    /// </summary>
    public string? PartnerId { get; init; }

    /// <summary>
    /// Optional document / transaction type (e.g. "EDI-850", "API-Sync", "FlatFile").
    /// </summary>
    public string? DocumentType { get; init; }
}
