using FluentValidation;
using FSH.Framework.Core.Localization;
using FSH.Framework.Web.Validation;
using FSH.Modules.Auditing.Contracts.v1.GetAudits;
using Microsoft.Extensions.Localization;

namespace FSH.Modules.Auditing.Features.v1.GetAudits;

public sealed class GetAuditsQueryValidator : AbstractValidator<GetAuditsQuery>
{
    public GetAuditsQueryValidator(IStringLocalizer<SharedResources> localizer)
    {
        Include(new PagedQueryValidator<GetAuditsQuery>(localizer));

        RuleFor(q => q)
            .Must(q => !q.FromUtc.HasValue || !q.ToUtc.HasValue || q.FromUtc <= q.ToUtc)
            .WithMessage("FromUtc must be less than or equal to ToUtc.");

        // Reject oversized windows up-front (user sees a 400, not a silent clamp). The handler
        // still clamps as defence in depth (e.g. when only one endpoint is supplied).
        RuleFor(q => q)
            .Must(q =>
                !q.FromUtc.HasValue
                || !q.ToUtc.HasValue
                || (q.ToUtc.Value - q.FromUtc.Value) <= GetAuditsQueryHandler.MaxWindow)
            .WithMessage($"Audit query window cannot exceed {GetAuditsQueryHandler.MaxWindow.TotalDays:0} days.");
    }
}
