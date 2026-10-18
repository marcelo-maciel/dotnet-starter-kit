using FSH.Modules.PAF.Contracts.v1.Unidades;
using FSH.Modules.PAF.Escopo;
using Mediator;

namespace FSH.Modules.PAF.Features.v1.Unidades.ObterAdministracaoUnidades;

/// <summary>Same rule as the create, disable and enable handlers, so the buttons never promise what the server refuses.</summary>
public sealed class ObterAdministracaoUnidadesQueryHandler(EscopoUsuario escopo)
    : IQueryHandler<ObterAdministracaoUnidadesQuery, AdministracaoUnidadesDto>
{
    public async ValueTask<AdministracaoUnidadesDto> Handle(ObterAdministracaoUnidadesQuery query, CancellationToken cancellationToken) =>
        new(await escopo.AdministraUnidadesAsync(cancellationToken).ConfigureAwait(false));
}
