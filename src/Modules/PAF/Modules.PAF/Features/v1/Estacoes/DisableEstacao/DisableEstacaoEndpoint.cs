using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Estacoes;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Estacoes.DisableEstacao;

/// <summary>Body of the deactivation; absent or null <c>cancelarAtendimentoId</c> keeps the atendimento in progress.</summary>
public sealed record DesativarEstacaoCorpo(Guid? CancelarAtendimentoId);

public static class DisableEstacaoEndpoint
{
    internal static RouteHandlerBuilder MapDisableEstacaoEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/estacoes/{id:guid}/disable",
                async (Guid id, DesativarEstacaoCorpo? corpo, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(
                        new DisableEstacaoCommand(id) { CancelarAtendimentoId = corpo?.CancelarAtendimentoId }, ct)))
            .WithName("PafDisableEstacao")
            .WithSummary("Deactivate a station, release its guichê and optionally cancel the atendimento in progress")
            .RequirePermission(PafPermissions.Estacoes.Update);

    internal static RouteHandlerBuilder MapGetImpactoDesativacaoEstacaoEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/estacoes/{id:guid}/desativacao",
                async (Guid id, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new ObterImpactoDesativacaoEstacaoQuery(id), ct)))
            .WithName("PafGetImpactoDesativacaoEstacao")
            .WithSummary("Preview what deactivating the station would do: atendimento in progress and pairs left uncovered")
            .RequirePermission(PafPermissions.Estacoes.Update);
}
