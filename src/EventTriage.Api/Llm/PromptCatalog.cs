namespace EventTriage.Api.Llm;

/// <summary>
/// Registry of prompt versions. Prompts are first-class artefacts: they have
/// versions, can be diffed, and the version that was used is recorded on
/// every response so we can correlate quality regressions to a specific change.
///
/// In production the catalog would be backed by a central store (Cosmos DB
/// container or a Git-backed prompt repo with CI validation). For the assessment
/// it's an in-memory registry – the contract is what matters.
/// </summary>
public interface IPromptCatalog
{
    /// <summary>Active default version, configurable via appsettings.</summary>
    string DefaultVersion { get; }

    /// <summary>True when the requested version exists.</summary>
    bool TryGet(string version, out PromptTemplate template);
}

/// <summary>
/// An immutable snapshot of a single versioned prompt, including the system
/// instruction text and the JSON schema used to constrain model output.
/// </summary>
/// <param name="Version">The version identifier (e.g. <c>v1</c>, <c>v2-experimental</c>).</param>
/// <param name="SystemPrompt">The system-turn instruction sent to the model.</param>
/// <param name="ResponseJsonSchema">
/// The JSON Schema string enforced via structured-output mode.
/// Must stay in sync with <see cref="LlmClassification"/> and <see cref="AzureOpenAiClassifier"/>.
/// </param>
public sealed record PromptTemplate(string Version, string SystemPrompt, string ResponseJsonSchema);

/// <summary>
/// In-process implementation of <see cref="IPromptCatalog"/> backed by a static
/// dictionary. Suitable for single-node deployments and testing. In production,
/// swap for a Cosmos DB- or Git-backed store without changing any callers.
/// </summary>
public sealed class InMemoryPromptCatalog : IPromptCatalog
{
    private readonly Dictionary<string, PromptTemplate> _templates;

    /// <summary>
    /// Initialises the catalog and registers the built-in prompt versions.
    /// </summary>
    /// <param name="defaultVersion">
    /// The version returned by <see cref="DefaultVersion"/>. Must match a key in
    /// the built-in template dictionary; typically driven from <c>appsettings.json</c>.
    /// </param>
    public InMemoryPromptCatalog(string defaultVersion)
    {
        DefaultVersion = defaultVersion;
        _templates = new Dictionary<string, PromptTemplate>(StringComparer.OrdinalIgnoreCase)
        {
            ["v1"] = new PromptTemplate(
                Version: "v1",
                SystemPrompt: V1SystemPrompt,
                ResponseJsonSchema: ResponseJsonSchema),

            // Example shadow / experimental prompt: shorter, more directive.
            // Allows side-by-side evaluation in non-prod environments.
            ["v2-experimental"] = new PromptTemplate(
                Version: "v2-experimental",
                SystemPrompt: V2SystemPrompt,
                ResponseJsonSchema: ResponseJsonSchema)
        };
    }

    /// <inheritdoc/>
    public string DefaultVersion { get; }

    /// <inheritdoc/>
    public bool TryGet(string version, out PromptTemplate template)
        => _templates.TryGetValue(version, out template!);

    // --------- Prompt bodies ---------
    #region Prompt Bodies

    private const string V1SystemPrompt = """
        You are an expert B2B integration support engineer working for a company
        that processes EDI documents, API integrations, and flat-file exchanges
        for thousands of trading partners.

        You will receive a single error event with a free-form JSON payload that
        may come from any of several acquired platforms. Field names are NOT
        consistent. Extract the meaningful signal you can find regardless of the
        exact key names (look for things like message, error, code, stackTrace,
        partner, document, transaction, status, exception, reason, etc.).

        Your job:
          1. Classify the error into one of these categories:
             - SchemaValidation     (payload did not match expected schema)
             - AuthenticationFailure
             - AuthorizationFailure
             - PartnerConnectivity  (timeouts, DNS, TLS, refused connections)
             - DocumentTranslation  (EDI/XML/flat-file transform failure)
             - DataQuality          (missing required business fields)
             - DuplicateSubmission
             - InternalSystemError  (our own infra)
             - Unknown              (only when there is genuinely no signal)
          2. Assign a severity:
             - Critical: revenue-impacting, partner blocked, data loss possible
             - High: a single partner's flow is blocked
             - Medium: degraded but partner is still functional
             - Low: cosmetic / non-blocking
             - Informational: not really an error, mis-routed log line
          3. Produce a confidence score between 0 and 1.
          4. Write a one-line summary (≤ 140 chars).
          5. Produce 1–5 ordered remediation steps. Each step ≤ 200 chars,
             written for a human integration analyst. Be concrete: "Re-fetch the
             partner cert from KeyVault" beats "Check certificates".
          6. If you can infer it, name a suggestedOwner team. Otherwise null.

        Output rules:
          * Return ONLY a single JSON object matching the schema.
          * No prose, no markdown, no code fences.
          * If you cannot determine something, use category "Unknown" with low
            confidence rather than inventing values.
        """;

    private const string V2SystemPrompt = """
        You triage B2B integration errors. Input: one JSON event with arbitrary
        shape. Output: a single JSON object matching the schema, no prose.

        Categories: SchemaValidation, AuthenticationFailure, AuthorizationFailure,
        PartnerConnectivity, DocumentTranslation, DataQuality, DuplicateSubmission,
        InternalSystemError, Unknown.

        Severity: Critical, High, Medium, Low, Informational.
        Be concrete in remediationSteps. If signal is weak, use Unknown with low confidence.
        """;

    private const string ResponseJsonSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["category","severity","confidence","summary","remediationSteps"],
          "properties": {
            "category": {
              "type": "string",
              "enum": [
                "SchemaValidation","AuthenticationFailure","AuthorizationFailure",
                "PartnerConnectivity","DocumentTranslation","DataQuality",
                "DuplicateSubmission","InternalSystemError","Unknown"
              ]
            },
            "severity": {
              "type": "string",
              "enum": ["Critical","High","Medium","Low","Informational"]
            },
            "confidence": { "type": "number", "minimum": 0, "maximum": 1 },
            "summary":    { "type": "string", "maxLength": 140 },
            "remediationSteps": {
              "type": "array",
              "minItems": 1,
              "maxItems": 5,
              "items": { "type": "string", "maxLength": 200 }
            },
            "suggestedOwner": { "type": ["string","null"] }
          }
        }
        """;
        
    #endregion
}
