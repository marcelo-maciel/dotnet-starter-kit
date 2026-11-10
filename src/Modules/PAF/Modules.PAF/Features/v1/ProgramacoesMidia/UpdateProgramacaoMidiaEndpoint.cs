using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Midias;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.ProgramacoesMidia;

public static class UpdateProgramacaoMidiaEndpoint
{
    internal static RouteHandlerBuilder MapUpdateProgramacaoMidiaEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPut("/programacoes-midia/{id:guid}",
                async (Guid id, UpdateProgramacaoMidiaCommand command, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(command with { Id = id }, ct)))
            .WithName("PafUpdateProgramacaoMidia")
            .WithSummary("Edit a media schedule with the version read")
            .RequirePermission(PafPermissions.Programacoes.Update);
}
