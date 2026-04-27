/// <summary>
/// Strongly-typed configuration for the Azure OpenAI integration.
/// Bound from the <c>Llm</c> section in <c>appsettings.json</c>; sensitive
/// values should be supplied via user-secrets locally or Key Vault in Azure.
/// </summary>
public sealed class LlmOptions
{
    /// <summary>
    /// Azure OpenAI resource endpoint, e.g. <c>https://&lt;resource&gt;.openai.azure.com/</c>.
    /// </summary>
    public required string Endpoint { get; init; }

    /// <summary>
    /// Name of the model deployment within the Azure OpenAI resource (e.g. <c>gpt-4o-mini</c>).
    /// </summary>
    public required string Deployment { get; init; }

    /// <summary>
    /// API key for local development. When absent, <see cref="Azure.Identity.DefaultAzureCredential"/>
    /// is used instead — the preferred path in Azure-hosted environments.
    /// </summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// The prompt catalog version used when the caller does not specify one
    /// (e.g. <c>v1</c>). Must match a registered key in <see cref="EventTriage.Api.Llm.IPromptCatalog"/>.
    /// </summary>
    public required string DefaultPromptVersion { get; init; }
}
