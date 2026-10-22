using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Midias;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Midias.TrocarArquivoMidia;

public static class UpdateMidiaArquivoEndpoint
{
    internal static RouteHandlerBuilder MapSwapMidiaFileEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPut("/midias/{id:guid}/arquivo",
                async (Guid id, TrocarArquivoMidiaCommand command, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(command with { Id = id }, ct)))
            .WithName("PafSwapMidiaFile")
            .WithSummary("Point a media to a new confirmed upload")
            .RequirePermission(PafPermissions.Midias.Update);
}
