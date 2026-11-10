using System.Security.Claims;
using FSH.Framework.Web.Idempotency;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Fichas;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace FSH.Modules.PAF.Features.v1.Fichas.EmitirFicha;

public static class IssueFichaEndpoint
{
    // Two layers (RF11): the kit filter replays a cached response while Valkey is up; the same key is also
    // stored on the ficha, so a resend with the cache down still returns the stored ficha.
    internal static RouteHandlerBuilder MapIssueFichaEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapPost("/paf/estacao/emissao/fichas",
                async (EmitirFichaRequest request, ClaimsPrincipal user, HttpContext http, IOptions<IdempotencyOptions> idempotencia,
                    IMediator mediator, CancellationToken ct) =>
                {
                    var chave = http.Request.Headers[idempotencia.Value.HeaderName].ToString();
                    var estacaoId = Guid.Parse(user.FindFirstValue(PafClaims.Estacao)!);
                    return Results.Ok(await mediator.Send(new EmitirFichaCommand(estacaoId, request.FilaId, request.PrioridadeId, chave), ct));
                })
            .WithName("IssueFicha")
            .WithSummary("Emit a ficha at the totem with the station's emission credential")
            .RequireAuthorization(PafPolicies.EstacaoEmissao)
            .WithIdempotency();
    }
}
