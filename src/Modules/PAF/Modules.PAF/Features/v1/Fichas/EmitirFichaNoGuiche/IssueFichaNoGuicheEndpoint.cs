using FSH.Framework.Shared.Identity.Authorization;
using FSH.Framework.Web.Idempotency;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Fichas;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace FSH.Modules.PAF.Features.v1.Fichas.EmitirFichaNoGuiche;

public static class IssueFichaNoGuicheEndpoint
{
    // Same two layers as the totem (RF11): the kit filter replays while Valkey is up (keyed by caller and route), and
    // the key stored on the ficha per emitting station answers a resend with the cache down.
    internal static RouteHandlerBuilder MapIssueFichaNoGuicheEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/fichas",
                async (EmitirFichaNoGuicheRequest request, HttpContext http, IOptions<IdempotencyOptions> idempotencia,
                    IMediator mediator, CancellationToken ct) =>
                {
                    var chave = http.Request.Headers[idempotencia.Value.HeaderName].ToString();
                    return Results.Ok(await mediator.Send(new EmitirFichaNoGuicheCommand(request.FilaId, request.PrioridadeId, chave), ct));
                })
            .WithName("PafIssueFichaNoGuiche")
            .WithSummary("Emit a ficha at the session's guichê (RF11 manual emission)")
            .RequirePermission(PafPermissions.Fichas.Emitir)
            .Produces<FichaEmitidaDto>(StatusCodes.Status200OK)
            .WithIdempotency();
}
