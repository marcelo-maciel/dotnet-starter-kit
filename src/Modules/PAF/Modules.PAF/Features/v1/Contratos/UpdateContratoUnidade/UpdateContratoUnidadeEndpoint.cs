using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Contratos;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Contratos.UpdateContratoUnidade;

public static class UpdateContratoUnidadeEndpoint
{
    internal static RouteHandlerBuilder MapUpdateContratoUnidadeEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPut("/contratos/{id:guid}",
                async (Guid id, UpdateContratoUnidadeCommand command, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(command with { Id = id }, ct)))
            .WithName("PafUpdateContratoUnidade")
            .WithSummary("Update a unit contract (Codefi operator)")
            .RequirePermission(PafPermissions.Contratos.Manage);
}
