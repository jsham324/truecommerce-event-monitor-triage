using System.ClientModel;
using System.Text.Json;
using Azure.AI.OpenAI;
using EventTriage.Api.Models;
using OpenAI.Chat;

namespace EventTriage.Api.Llm;

/// <summary>
/// Azure OpenAI implementation. Uses the Chat Completions API with strict
/// JSON Schema response format so that model output is validated structurally
/// at the boundary, before we ever try to deserialize it.
///
/// All cross-cutting concerns (retry, circuit breaker, timeouts, fallback)
/// are handled by the caller (TriageService) so this class stays small and
/// focused on the protocol details.
/// </summary>
public sealed class AzureOpenAiClassifier : ILlmClassifier
{
    private readonly ChatClient _chatClient;
    private readonly IPromptCatalog _prompts;
    private readonly ILogger<AzureOpenAiClassifier> _logger;

    public AzureOpenAiClassifier(
        ChatClient chatClient,
        IPromptCatalog prompts,
        ILogger<AzureOpenAiClassifier> logger)
    {
        _chatClient = chatClient;
        _prompts = prompts;
        _logger = logger;
    }

    /// <summary>
    /// Classifies a single error event using the Azure OpenAI Chat Completions API.
    /// </summary>
    /// <param name="evt">The error event to classify.</param>
    /// <param name="promptVersion">
    /// The prompt catalog version to use. Must exist in <see cref="IPromptCatalog"/>;
    /// an unknown version is a programming error and throws immediately.
    /// </param>
    /// <param name="cancellationToken">Propagated from the caller's resilience pipeline.</param>
    /// <returns>A structured classification produced by the model.</returns>
    /// <exception cref="InvalidOperationException">Thrown when <paramref name="promptVersion"/> is not registered.</exception>
    /// <exception cref="LlmContractException">Thrown when the model response does not match the agreed JSON schema.</exception>
    public async Task<LlmClassification> ClassifyAsync(
        ErrorEvent evt,
        string promptVersion,
        CancellationToken cancellationToken)
    {
        if (!_prompts.TryGet(promptVersion, out var template))
        {
            // Unknown prompt version is a programming error, not a user error -
            // fail fast so we notice it in tests rather than silently using default.
            throw new InvalidOperationException($"Unknown prompt version '{promptVersion}'");
        }

        var userMessage = BuildUserMessage(evt);

        var options = new ChatCompletionOptions
        {
            Temperature = 0.1f,            // We want stable classifications, not creativity
            MaxOutputTokenCount = 600,
            ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                jsonSchemaFormatName: "triage_result",
                jsonSchema: BinaryData.FromString(template.ResponseJsonSchema),
                jsonSchemaIsStrict: true)
        };

        var messages = new ChatMessage[]
        {
            new SystemChatMessage(template.SystemPrompt),
            new UserChatMessage(userMessage)
        };

        // Any Azure SDK failure (CredentialUnavailableException, RequestFailedException, etc.)
        // is translated to HttpRequestException so the TriageService resilience pipeline
        // can apply its retry and fallback logic without knowing about Azure internals.
        ClientResult<ChatCompletion> response;
        try
        {
            response = await _chatClient
                .CompleteChatAsync(messages, options, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new HttpRequestException($"LLM call failed: {ex.Message}", ex);
        }

        var completion = response.Value;
        var json = completion.Content[0].Text;

        _logger.LogDebug("LLM classification raw output for {EventId}: {Json}",
            evt.EventId, json);

        // The schema-strict response format means this should always parse,
        // but we still defend against drift between prompt versions.
        TriageJsonPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<TriageJsonPayload>(json, JsonOptions)
                      ?? throw new InvalidOperationException("Empty LLM response");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "LLM produced unparseable JSON for {EventId}", evt.EventId);
            throw new LlmContractException("LLM response did not match the agreed JSON schema", ex);
        }

        return new LlmClassification
        {
            Category = payload.Category,
            Severity = Enum.Parse<Severity>(payload.Severity, ignoreCase: true),
            Confidence = Math.Clamp(payload.Confidence, 0d, 1d),
            Summary = payload.Summary,
            RemediationSteps = payload.RemediationSteps,
            SuggestedOwner = payload.SuggestedOwner,
            PromptTokens = completion.Usage.InputTokenCount,
            CompletionTokens = completion.Usage.OutputTokenCount
        };
    }

    /// <summary>
    /// Serialises the event into the JSON envelope sent as the user turn.
    /// Includes all structured metadata alongside the raw payload so the model
    /// can use field names and payload shape as part of the classification signal.
    /// </summary>
    /// <param name="evt">The event to serialise.</param>
    /// <returns>A compact JSON string suitable for use as a <see cref="UserChatMessage"/>.</returns>
    private static string BuildUserMessage(ErrorEvent evt)
    {
        // We feed the model a small structured envelope plus the raw payload
        // so it can use field names / shape as part of the signal.
        var envelope = new
        {
            evt.EventId,
            evt.Source,
            evt.OccurredAt,
            evt.PartnerId,
            evt.DocumentType,
            Payload = evt.Payload
        };
        return JsonSerializer.Serialize(envelope, JsonOptions);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Local DTO that exactly mirrors the JSON schema enforced on the model response.
    /// Keeping it private ensures the shape can only change alongside the schema string.
    /// </summary>
    private sealed record TriageJsonPayload(
        string Category,
        string Severity,
        double Confidence,
        string Summary,
        IReadOnlyList<string> RemediationSteps,
        string? SuggestedOwner);
}

/// <summary>
/// Thrown when the LLM response does not match the agreed schema.
/// Triggers the fallback path rather than a 5xx response to the caller.
/// </summary>
public sealed class LlmContractException : Exception
{
    public LlmContractException(string message, Exception inner) : base(message, inner) { }
}
