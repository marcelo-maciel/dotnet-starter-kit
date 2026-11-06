using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Contratos;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Contratos.CreateContratoUnidade;

public static class CreateContratoUnidadeEndpoint
{
    internal static RouteHandlerBuilder MapCreateContratoUnidadeEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/contratos",
                async (CreateContratoUnidadeCommand command, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(command, ct)))
            .WithName("PafCreateContratoUnidade")
            .WithSummary("Create a unit contract (Codefi operator)")
            .RequirePermission(PafPermissions.Contratos.Manage);
}
