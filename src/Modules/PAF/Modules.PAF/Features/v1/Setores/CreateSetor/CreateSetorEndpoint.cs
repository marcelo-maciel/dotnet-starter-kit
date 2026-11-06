using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Setores;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Setores.CreateSetor;

public static class CreateSetorEndpoint
{
    // The setor is kept inside the fila cadastro (RF02, decided 24/08/2026), so it takes the fila permissions.
    internal static RouteHandlerBuilder MapCreateSetorEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/setores",
                async (CreateSetorCommand command, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(command, ct)))
            .WithName("PafCreateSetor")
            .WithSummary("Create a sector in the current unit")
            .RequirePermission(PafPermissions.Filas.Create);
}
