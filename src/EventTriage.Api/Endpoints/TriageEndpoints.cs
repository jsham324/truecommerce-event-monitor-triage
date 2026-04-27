using EventTriage.Api.Models;
using EventTriage.Api.Services;
using FluentValidation;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace EventTriage.Api.Endpoints;

/// <summary>
/// Provides endpoint mappings and handlers for the triage API surface.
/// </summary>
public static class TriageEndpoints
{
    public static IEndpointRouteBuilder MapTriageEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/triage").WithTags("Triage");

        group.MapPost("/", HandleAsync)
            .WithName("TriageBatch")
            .WithSummary("Classify a batch of error events")
            .WithDescription(
                "Accepts a batch of semi-structured error events with inconsistent schemas, " +
                "extracts context, and returns category, severity, confidence, and remediation steps " +
                "per event. Falls back to a backup classifier when the LLM is unavailable.")
            .Produces<TriageBatchResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .Produces<ProblemDetails>(StatusCodes.Status503ServiceUnavailable);

        // Sample-data endpoint to make manual exercise / smoke testing easy.
        group.MapGet("/sample", () => Results.Ok(MockData.SampleBatch()))
            .WithName("TriageSample")
            .WithSummary("Returns a sample request body");

        return app;
    }

    /// <summary>
    /// Validates the incoming <see cref="TriageBatchRequest"/>, runs triage processing
    /// and returns a <see cref="TriageBatchResponse"/> or validation problems.
    /// </summary>
    /// <param name="request">The batch of events to classify and triage.</param>
    /// <param name="validator">FluentValidation validator for the request type.</param>
    /// <param name="service">Service that performs the triage processing.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>
    /// A task that resolves to either an <see cref="Results.Ok{T}"/> wrapping
    /// a <see cref="TriageBatchResponse"/>, or a <see cref="ValidationProblem"/> result
    /// when validation fails.
    /// </returns>
    private static async Task<Results<Ok<TriageBatchResponse>, ValidationProblem>> HandleAsync(
        [FromBody] TriageBatchRequest request,
        IValidator<TriageBatchRequest> validator,
        ITriageService service,
        CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return TypedResults.ValidationProblem(validation.ToDictionary());
        }

        var response = await service.TriageAsync(request, cancellationToken);
        return TypedResults.Ok(response);
    }
}
