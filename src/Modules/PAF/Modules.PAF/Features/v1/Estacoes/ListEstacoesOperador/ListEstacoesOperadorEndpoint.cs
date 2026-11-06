using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Estacoes;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Estacoes.ListEstacoesOperador;

public static class ListEstacoesOperadorEndpoint
{
    // Unidades.Operar is the root-only operator permission (P01 closed the catalog): a client admin gets 403.
    internal static RouteHandlerBuilder MapListEstacoesOperadorEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/operador/estacoes",
                async (IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new ListEstacoesOperadorQuery(), ct)))
            .WithName("PafListEstacoesOperador")
            .WithSummary("Connection state of every station role of the tenant in the header (Codefi operator, RNF20)")
            .RequirePermission(PafPermissions.Unidades.Operar);
}
