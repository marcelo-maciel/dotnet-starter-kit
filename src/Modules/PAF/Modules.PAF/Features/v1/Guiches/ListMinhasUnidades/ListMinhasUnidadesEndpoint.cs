using FSH.Framework.Shared.Identity.Authorization;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Guiches;
using FSH.Modules.PAF.Contracts.v1.Unidades;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FSH.Modules.PAF.Features.v1.Guiches.ListMinhasUnidades;

// ListUnidadesQuery already answers with the caller's own scope; this route only opens it to the attendant,
// who chooses among those unidades without the cadastro permission Unidades.View. An inactive unidade is not
// offered, and cadastro fields (DocumentoFiscal) stay behind Unidades.View.
public static class ListMinhasUnidadesEndpoint
{
    internal static RouteHandlerBuilder MapListMinhasUnidadesEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/unidades",
                async (IMediator mediator, CancellationToken ct) =>
                    Results.Ok((await mediator.Send(new ListUnidadesQuery(), ct))
                        .Where(u => u.Ativa)
                        .Select(u => new UnidadeDoGuicheDto(u.Id, u.Nome))
                        .ToList()))
            .WithName("PafListMinhasUnidades")
            .WithSummary("List the unidades of the caller's own scope for the guichê screen")
            .RequirePermission(PafPermissions.Guiches.Selecionar);
}
