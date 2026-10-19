using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Midias;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Midias.PedirEnvioMidia;

public static class GenerateMidiaUploadUrlEndpoint
{
    internal static RouteHandlerBuilder MapRequestMediaUploadEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/midias/envios",
                async (PedirEnvioMidiaCommand command, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(command, ct)))
            .WithName("PafRequestMediaUpload")
            .WithSummary("Presigned upload URL for a new media file of the current unit")
            .RequirePermission(PafPermissions.Midias.Create);

    internal static RouteHandlerBuilder MapRequestMediaSwapUploadEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/midias/{id:guid}/envios",
                async (Guid id, PedirEnvioTrocaMidiaCommand command, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(command with { MidiaId = id }, ct)))
            .WithName("PafRequestMediaSwapUpload")
            .WithSummary("Presigned upload URL for the new file of an existing media")
            .RequirePermission(PafPermissions.Midias.Update);
}
