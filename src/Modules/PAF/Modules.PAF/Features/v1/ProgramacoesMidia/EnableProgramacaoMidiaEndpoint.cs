using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Midias;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.ProgramacoesMidia;

public static class EnableProgramacaoMidiaEndpoint
{
    internal static RouteHandlerBuilder MapEnableProgramacaoMidiaEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/programacoes-midia/{id:guid}/enable",
                async (Guid id, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new EnableProgramacaoMidiaCommand(id), ct)))
            .WithName("PafEnableProgramacaoMidia")
            .WithSummary("Activate a media schedule")
            .RequirePermission(PafPermissions.Programacoes.Update);
}
