using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Atendimentos;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Atendimentos.ListDestinosEncaminhamento;

public static class ListForwardDestinationsEndpoint
{
    internal static RouteHandlerBuilder MapListForwardDestinationsEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/encaminhamento/destinos",
                async (IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new ListDestinosEncaminhamentoQuery(), ct)))
            .WithName("PafListForwardDestinations")
            .WithSummary("List the destination filas and covered prioridades for forwarding the started atendimento (RF17)")
            .RequirePermission(PafPermissions.Atendimentos.Encaminhar);
}
