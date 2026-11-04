using FSH.Modules.Auditing.Contracts;
using FSH.Modules.Identity.Contracts.v1.Tokens.IssueTokenByEmail;
using Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Identity.Features.v1.Tokens.IssueTokenByEmail;

public static class IssueTokenByEmailEndpoint
{
    public static RouteHandlerBuilder MapIssueTokenByEmailEndpoint(this IEndpointRouteBuilder endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        // No tenant header: the tenant comes from the e-mail, never from the caller. Root is never found, so the
        // operator console keeps signing in through token/issue with tenant=root.
        return endpoint.MapPost("/token/issue-by-email",
            [AllowAnonymous] async ([FromBody] IssueTokenByEmailCommand command,
            [FromServices] IMediator mediator,
            CancellationToken ct) => TypedResults.Ok(await mediator.Send(command, ct)))
            // Anonymous and without a tenant, so the request audit would land in root: the e-mail must not.
            .NoAuditBody()
            .WithName("IssueJwtTokensByEmail")
            .WithSummary("Issue JWT tokens by e-mail, without tenant")
            .WithDescription("Finds the single active client tenant holding the e-mail and issues the access and refresh tokens there, returning that tenant for the later requests. Unknown e-mail, wrong password, deactivated tenant and root accounts all get the same 401.")
            .Produces<IssueTokenByEmailResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status429TooManyRequests);
    }
}
