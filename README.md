# Intelligent Event Monitoring & Triage

Principal-Architect technical assessment — **Use Case Option 1**.

A production-shaped reference architecture and a working slice of the AI triage
service for TrueCommerce's post-acquisition B2B integration platform. Source
systems emit error events with **inconsistent schemas**; this service classifies
each event, scores its severity, and returns a validated remediation suggestion,
**degrading gracefully** when the LLM is unavailable.

---

## TL;DR

- **API:** `POST /api/v1/triage` — batch of error events in, batch of structured
  triage results out.
- **Stack:** .NET 9, ASP.NET Core Minimal APIs, Azure OpenAI, Polly v8,
  FluentValidation, OpenTelemetry, xUnit + NSubstitute.
- **Resilience:** timeout → exponential-backoff retry with jitter → circuit
  breaker → deterministic heuristic fallback → dead-letter.
- **Prompt versioning** is first-class and recorded on every result.
- **Container Apps**-ready, with a CI workflow that builds, tests, and produces
  a Docker image.

The full architecture (including ingestion, lake, and downstream actions) lives
in [`docs/architecture.mmd`](docs/architecture.mmd) and
[`docs/architecture.drawio`](docs/architecture.drawio). Decision rationale and
tradeoffs are in [`docs/DECISIONS.md`](docs/DECISIONS.md). Load-testing approach is
[below](#load-testing-strategy).

---

## Repository layout

```text
intelligent-event-triage/
├── EventTriage.sln
├── README.md                    ← you are here
├── Dockerfile                   ← multi-stage, distroless-style runtime
├── .github/workflows/ci.yml     ← build + test + container image
├── docs/
│   ├── DECISIONS.md             ← ADRs / tradeoffs
│   ├── architecture.mmd         ← Mermaid diagram (renders in GitHub)
│   └── architecture.drawio      ← draw.io / diagrams.net source
├── src/EventTriage.Api/
│   ├── Program.cs               ← composition root
│   ├── Endpoints/               ← Minimal API surface + sample data
│   ├── Models/                  ← request / response DTOs
│   ├── Llm/                     ← ILlmClassifier, AOAI impl, prompt catalog
│   ├── Resilience/              ← heuristic fallback classifier
│   ├── Services/                ← TriageService (orchestration + Polly)
│   └── Validation/              ← FluentValidation rules
└── tests/EventTriage.Tests/
    ├── TriageServiceTests.cs              ← happy path, retry, fallback, mixed
    ├── HeuristicFallbackClassifierTests.cs← signal recognition
    └── TriageEndpointIntegrationTests.cs  ← WebApplicationFactory + stubbed LLM
```

---

## Running locally

### Prerequisites

- .NET 9 SDK
- Docker (optional, for the container path)
- An Azure OpenAI deployment **or** any OpenAI-compatible endpoint
  (You can also start the API with no real LLM — every request will exercise
  the heuristic fallback path. Useful for end-to-end smoke tests.)

### Configure secrets without committing them

```bash
cd src/EventTriage.Api
dotnet user-secrets init
dotnet user-secrets set "Llm:Endpoint"   "https://<your-resource>.openai.azure.com/"
dotnet user-secrets set "Llm:Deployment" "gpt-4o-mini"
dotnet user-secrets set "Llm:ApiKey"     "<key>"   # optional; omit to use Managed Identity
```

### Run

```bash
dotnet run --project src/EventTriage.Api
# Swagger UI: https://localhost:5001/swagger
```

### Try it

```bash
# Get a sample request body
curl https://localhost:5001/api/v1/triage/sample -k

# Send it back
curl -k -X POST https://localhost:5001/api/v1/triage \
     -H "Content-Type: application/json" \
     -d @sample.json | jq
```

### Test

```bash
dotnet test
```

### Container

```bash
docker build -t event-triage-api .
docker run -p 8080:8080 \
           -e Llm__Endpoint="https://..." \
           -e Llm__Deployment="gpt-4o-mini" \
           -e Llm__ApiKey="..." \
           event-triage-api
```

---

## CI / CD pipeline

The workflow at [`.github/workflows/ci.yml`](.github/workflows/ci.yml) runs on
every push and pull request to `main`. It is split into two sequential jobs so
that the container image is never built from a broken build.

### Job 1 — `build-and-test`

| Step | Detail |
| ---- | ------ |
| Restore | NuGet packages, with a cache keyed on `*.csproj` hashes to skip re-downloads on unchanged dependency graphs. |
| Build | `Release` configuration with `TreatWarningsAsErrors=true` — warnings cannot silently accumulate. |
| Test | Full test suite with XPlat code coverage collected to `TestResults/`. |
| Artifacts | Test results (`.trx`) and the published API binaries are uploaded so any run can be inspected without re-building. |

### Job 2 — `container` (depends on `build-and-test`)

Builds the Docker image via `docker/build-push-action` with GitHub Actions
layer caching (`type=gha`) to keep image build times fast. The image is tagged
with both the commit SHA and `latest`.

**Push is disabled on PRs.** A passing PR build proves the image compiles and
all tests pass; the push step is intentionally left for post-merge.

### Production deployment path (not wired in this assessment)

The commented-out steps in the workflow describe the full production path:

1. **Authenticate** to Azure Container Registry using OIDC / federated identity
   (no long-lived secrets stored in GitHub).
2. **Push** the tagged image to ACR.
3. **Deploy** to the `dev` Container App with
   `az containerapp update --image ...`.
4. **Promote** to `staging` and `prod` via GitHub Environment protection rules
   (required reviewers, deployment gates).

This pattern keeps environment-specific secrets out of the repository and makes
every promotion auditable in the GitHub Deployments log.

---

## API contract

### `POST /api/v1/triage`

Request:

```json
{
  "promptVersion": "v1",
  "events": [
    {
      "eventId": "evt-001",
      "source": "edi-gateway-na",
      "occurredAt": "2026-04-25T12:00:00Z",
      "partnerId": "WALMART-US",
      "documentType": "EDI-850",
      "payload": {
        "error_code": "X12_VALIDATION_FAIL",
        "segment": "PO1",
        "message": "Required element 03 missing in PO1 segment"
      }
    }
  ]
}
```

Response:

```json
{
  "correlationId": "8c2…",
  "results": [
    {
      "eventId": "evt-001",
      "category": "SchemaValidation",
      "severity": "Medium",
      "confidence": 0.92,
      "summary": "EDI 850 PO1 missing required element 03",
      "remediationSteps": [
        "Re-emit the 850 with PO1-03 populated.",
        "Add a partner-specific schema rule to catch this at the gateway."
      ],
      "suggestedOwner": "EDI-Ops",
      "source": "llm",
      "promptVersion": "v1"
    }
  ],
  "metrics": {
    "totalEvents": 1,
    "classifiedByLlm": 1,
    "classifiedByFallback": 0,
    "deadLettered": 0,
    "elapsedMilliseconds": 487,
    "promptTokens": 412,
    "completionTokens": 86
  }
}
```

The `source` field on each result is the most important operational signal:

| `source`             | Meaning                                                                   |
| -------------------- | ------------------------------------------------------------------------- |
| `llm`                | Primary path. Classification produced by Azure OpenAI.                    |
| `fallback-heuristic` | LLM unavailable / contract violation. Confidence capped at 0.5.           |
| `dead-letter`        | Both paths failed. Manual review required.                                |

A spike in `fallback-heuristic` is your alert that the LLM is sick before any
customer-facing alarms fire.

---

## Load-testing strategy

The interesting load characteristic of this service is that **it is bottlenecked
by an external dependency we don't control** (Azure OpenAI). So load testing has
to answer two distinct questions:

### 1. "How does my service behave under load?"

Run with **the LLM stubbed at the `ILlmClassifier` boundary** — same code path,
deterministic latency. This isolates *our* code: thread-pool behaviour, GC
pressure, JSON throughput, Polly overhead, container CPU/memory limits.

- Tool: **k6** or **NBomber** (NBomber stays in C#, plays nicely with the team's
  stack and CI).
- Scenarios:
  - **Soak** — 50 RPS of 10-event batches for 60 minutes. Watch for memory
    leaks, handle exhaustion, and Polly's circuit-breaker counters.
  - **Spike** — ramp 0 → 500 RPS over 30 s, hold 2 min, ramp down. Validates
    Container Apps / KEDA scale-out and the parallelism gate inside
    `TriageService`.
  - **Stress** — push past the configured `MaxParallelism` and verify the
    semaphore queues rather than dropping requests.

### 2. "How does the *system* behave when the LLM is the bottleneck?"

Run against the **real** Azure OpenAI deployment with realistic prompts. Goals:

- Establish per-deployment **TPM/RPM ceilings** at the SKU we're sized for.
- Verify the **circuit breaker opens** when AOAI starts 429-ing, and that the
  fallback ratio in `TriageMetrics` rises smoothly rather than the API 5xx-ing.
- Measure **cost per 1k events** at p50 / p95 prompt sizes. Tokens are recorded
  on every successful result, so this is just a Snowflake query over the
  triaged-events stream.

### Synthetic data

The mock data in `Endpoints/MockData.cs` already covers five realistic shapes
(snake_case EDI, camelCase REST, free-form scheduler logs, AS2 cert errors,
informational noise). For load runs we'd parameterise the body generator over
those shapes and inject occasional malformed events to exercise validation.

### Acceptance gates (illustrative — to be tuned per environment)

| Metric                            | Target                        |
| --------------------------------- | ----------------------------- |
| p95 latency (10-event batch)      | < 3 s with LLM, < 200 ms stub |
| Error rate at 200 RPS sustained   | < 0.1 %                       |
| Fallback ratio at AOAI saturation | rises monotonically, no 5xx   |
| Memory growth over 60-min soak    | < 5 % drift                   |

A subset of these run on every PR via the CI pipeline using stubbed-LLM
NBomber scenarios; full real-LLM runs are weekly in a perf environment.

---

## What I'd build next

Things I deliberately scoped out of this assessment but would be the next PRs:

1. **Idempotency cache** — Cosmos DB-backed `eventId` → result table with TTL
   so retries don't double-charge tokens.

See [`docs/DECISIONS.md`](docs/DECISIONS.md) for why each of these was deferred and what
the tradeoff is.
