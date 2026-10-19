using FSH.Modules.PAF.Contracts.v1.Unidades;
using FSH.Modules.PAF.Escopo;
using Mediator;

namespace FSH.Modules.PAF.Features.v1.Unidades.GetAdministracaoUnidades;

/// <summary>Same rule as the create, disable and enable handlers, so the buttons never promise what the server refuses.</summary>
public sealed class GetAdministracaoUnidadesQueryHandler(EscopoUsuario escopo)
    : IQueryHandler<GetAdministracaoUnidadesQuery, AdministracaoUnidadesDto>
{
    public async ValueTask<AdministracaoUnidadesDto> Handle(GetAdministracaoUnidadesQuery query, CancellationToken cancellationToken) =>
        new(await escopo.AdministraUnidadesAsync(cancellationToken).ConfigureAwait(false));
}
