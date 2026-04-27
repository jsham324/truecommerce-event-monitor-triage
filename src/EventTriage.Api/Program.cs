using System.ClientModel;
using Azure.AI.OpenAI;
using Azure.Identity;
using EventTriage.Api.Endpoints;
using EventTriage.Api.Llm;
using EventTriage.Api.Resilience;
using EventTriage.Api.Services;
using EventTriage.Api.Services.Options;
using EventTriage.Api.Validation;
using FluentValidation;
using OpenAI.Chat;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// ----------------- Configuration -----------------
var triageOptions = builder.Configuration
    .GetSection("Triage")
    .Get<TriageOptions>() ?? new TriageOptions();

var llmOptions = builder.Configuration
    .GetSection("Llm")
    .Get<LlmOptions>() ?? throw new InvalidOperationException("Llm config missing");

builder.Services.AddSingleton(triageOptions);
builder.Services.AddSingleton(llmOptions);

// ----------------- LLM client -----------------
// Managed identity is the preferred auth path in Azure (Container Apps, AKS,
// App Service). Falls back to API key only when explicitly configured for
// local dev. We never log the key.
builder.Services.AddSingleton<ChatClient>(_ =>
{
    AzureOpenAIClient azureClient = !string.IsNullOrEmpty(llmOptions.ApiKey)
        ? new AzureOpenAIClient(new Uri(llmOptions.Endpoint), new ApiKeyCredential(llmOptions.ApiKey))
        : new AzureOpenAIClient(new Uri(llmOptions.Endpoint), new DefaultAzureCredential());

    return azureClient.GetChatClient(llmOptions.Deployment);
});

builder.Services.AddSingleton<IPromptCatalog>(_ =>
    new InMemoryPromptCatalog(llmOptions.DefaultPromptVersion));
builder.Services.AddSingleton<ILlmClassifier, AzureOpenAiClassifier>();
builder.Services.AddSingleton<BackupClassifier>();

// ----------------- Application services -----------------
builder.Services.AddSingleton<ITriageService, TriageService>();

// FluentValidation
builder.Services.AddScoped<IValidator<EventTriage.Api.Models.TriageBatchRequest>>(sp =>
    new TriageBatchRequestValidator(
        sp.GetRequiredService<TriageOptions>(),
        sp.GetRequiredService<IPromptCatalog>()));

// ----------------- Cross-cutting -----------------
builder.Services.AddProblemDetails();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddHealthChecks();

// OpenTelemetry: traces only here for brevity. Metrics & logs would follow
// the same pattern. OTLP exporter targets the OTel Collector / App Insights.
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(serviceName: "event-triage-api"))
    .WithTracing(t => t
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddSource("EventTriage.*")
        .AddOtlpExporter());

var app = builder.Build();

// ----------------- Pipeline -----------------
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapGet("/", () => Results.Redirect("/swagger"));

app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");
app.MapTriageEndpoints();

app.Run();

// Exposed so the integration test project can reference it via WebApplicationFactory.
public partial class Program;
