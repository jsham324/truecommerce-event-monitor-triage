using EventTriage.Api.Llm;
using EventTriage.Api.Models;
using EventTriage.Api.Services.Options;
using FluentValidation;

namespace EventTriage.Api.Validation;

/// <summary>
/// FluentValidation rules for <see cref="TriageBatchRequest"/>. Runs before the
/// request reaches <see cref="Services.TriageService"/>, returning a 400 with
/// structured problem details on any violation.
/// </summary>
public sealed class TriageBatchRequestValidator : AbstractValidator<TriageBatchRequest>
{
    /// <summary>
    /// Configures validation rules for the batch request.
    /// </summary>
    /// <param name="options">
    /// Triage pipeline options; provides <see cref="TriageOptions.MaxBatchSize"/>
    /// used to cap the events array.
    /// </param>
    /// <param name="prompts">
    /// Prompt catalog used to verify that <see cref="TriageBatchRequest.PromptVersion"/>,
    /// when supplied, refers to a registered version.
    /// </param>
    public TriageBatchRequestValidator(TriageOptions options, IPromptCatalog prompts)
    {
        RuleFor(r => r.PromptVersion)
            .Must(v => string.IsNullOrWhiteSpace(v) || prompts.TryGet(v, out _))
            .WithMessage(r => $"Unknown prompt version '{r.PromptVersion}'. Omit to use the default.");

        RuleFor(r => r.Events)
            .NotNull().WithMessage("Events array is required.")
            .Must(e => e.Count > 0).WithMessage("At least one event is required.")
            .Must(e => e.Count <= options.MaxBatchSize)
                .WithMessage($"Batch size must not exceed {options.MaxBatchSize}.");

        RuleForEach(r => r.Events).ChildRules(e =>
        {
            e.RuleFor(x => x.EventId).NotEmpty().MaximumLength(128);
            e.RuleFor(x => x.Source).NotEmpty().MaximumLength(128);
        });
    }
}
