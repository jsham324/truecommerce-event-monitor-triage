using System.Text.Json;
using EventTriage.Api.Models;

namespace EventTriage.Api.Endpoints;

/// <summary>
/// Synthetic data showcasing how disparate acquired platforms emit very different
/// payload shapes for the same logical "an error happened" signal.
/// </summary>
internal static class MockData
{
    public static TriageBatchRequest SampleBatch()
    {
        return new TriageBatchRequest
        {
            Events = new[]
            {
                // Platform A: legacy on-prem EDI gateway, snake_case payload.
                new ErrorEvent
                {
                    EventId = "evt-001",
                    Source = "edi-gateway-na",
                    OccurredAt = DateTimeOffset.UtcNow.AddMinutes(-3),
                    PartnerId = "WALMART-US",
                    DocumentType = "EDI-850",
                    Payload = JsonDocument.Parse("""
                        {
                          "error_code": "X12_VALIDATION_FAIL",
                          "segment": "PO1",
                          "message": "Required element 03 missing in PO1 segment",
                          "raw_isa": "ISA*00*..."
                        }
                        """).RootElement
                },

                // Platform B: cloud REST integration, camelCase, different keys.
                new ErrorEvent
                {
                    EventId = "evt-002",
                    Source = "api-sync-eu",
                    OccurredAt = DateTimeOffset.UtcNow.AddMinutes(-2),
                    PartnerId = "TESCO-UK",
                    DocumentType = "API-Sync",
                    Payload = JsonDocument.Parse("""
                        {
                          "exception": "System.Net.Http.HttpRequestException",
                          "detail": "The SSL connection could not be established. Connection timed out after 30s",
                          "endpoint": "https://api.partner.example.com/v2/orders",
                          "attempt": 3
                        }
                        """).RootElement
                },

                // Platform C: scheduler from acquired company, free-form text.
                new ErrorEvent
                {
                    EventId = "evt-003",
                    Source = "scheduler-acme",
                    OccurredAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                    Payload = JsonDocument.Parse("""
                        {
                          "log": "Job DailyExport-Acme failed: NullReferenceException at Acme.Mapper.Translate(EdiDoc) line 142",
                          "host": "acme-prod-01"
                        }
                        """).RootElement
                },

                // Platform D: looks scary but is actually informational noise.
                new ErrorEvent
                {
                    EventId = "evt-004",
                    Source = "edi-gateway-na",
                    OccurredAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                    DocumentType = "EDI-997",
                    Payload = JsonDocument.Parse("""
                        {
                          "level": "INFO",
                          "message": "Duplicate 997 acknowledgement received; dropped per dedupe policy"
                        }
                        """).RootElement
                },

                // Platform E: auth / certificate problem.
                new ErrorEvent
                {
                    EventId = "evt-005",
                    Source = "as2-bridge",
                    OccurredAt = DateTimeOffset.UtcNow,
                    PartnerId = "TARGET-US",
                    Payload = JsonDocument.Parse("""
                        {
                          "code": 401,
                          "reason": "Unauthorized",
                          "details": "AS2 partner certificate expired 2025-04-01"
                        }
                        """).RootElement
                }
            }
        };
    }
}
