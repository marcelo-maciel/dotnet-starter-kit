using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Midias;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.ProgramacoesMidia;

public static class CreateProgramacaoMidiaEndpoint
{
    internal static RouteHandlerBuilder MapCreateProgramacaoMidiaEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/programacoes-midia",
                async (CreateProgramacaoMidiaCommand command, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(command, ct)))
            .WithName("PafCreateProgramacaoMidia")
            .WithSummary("Create a media schedule in the current unit")
            .RequirePermission(PafPermissions.Programacoes.Create);
}
