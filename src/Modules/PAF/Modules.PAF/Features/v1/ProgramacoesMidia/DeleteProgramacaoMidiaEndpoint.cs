using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Midias;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.ProgramacoesMidia;

public static class DeleteProgramacaoMidiaEndpoint
{
    internal static RouteHandlerBuilder MapDeleteProgramacaoMidiaEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapDelete("/programacoes-midia/{id:guid}",
                async (Guid id, IMediator mediator, CancellationToken ct) =>
                {
                    await mediator.Send(new DeleteProgramacaoMidiaCommand(id), ct);
                    return Results.NoContent();
                })
            .WithName("PafDeleteProgramacaoMidia")
            .WithSummary("Delete a media schedule (physical)")
            .RequirePermission(PafPermissions.Programacoes.Delete);
}
