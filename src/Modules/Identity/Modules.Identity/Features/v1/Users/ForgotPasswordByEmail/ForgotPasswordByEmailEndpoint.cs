using FSH.Modules.Auditing.Contracts;
using FSH.Modules.Identity.Contracts.v1.Users.ForgotPasswordByEmail;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.Identity.Features.v1.Users.ForgotPasswordByEmail;

public static class ForgotPasswordByEmailEndpoint
{
    internal static RouteHandlerBuilder MapForgotPasswordByEmailEndpoint(this IEndpointRouteBuilder endpoints)
    {
        // No tenant header: the dashboard no longer asks for the company.
        return endpoints.MapPost("/forgot-password-by-email", async (
            [FromBody] ForgotPasswordByEmailCommand command,
            IMediator mediator,
            CancellationToken cancellationToken) =>
        {
            var result = await mediator.Send(command, cancellationToken);
            return TypedResults.Ok(result);
        })
        // Anonymous and without a tenant, so the request audit would land in root: the e-mail must not.
        .NoAuditBody()
        .WithName("RequestPasswordResetByEmail")
        .WithSummary("Request a password reset by e-mail, without tenant")
        .WithDescription("Sends the reset link when an active client tenant holds the e-mail. The answer is the same whether it exists or not.")
        .AllowAnonymous()
        .Produces(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status429TooManyRequests);
    }
}
