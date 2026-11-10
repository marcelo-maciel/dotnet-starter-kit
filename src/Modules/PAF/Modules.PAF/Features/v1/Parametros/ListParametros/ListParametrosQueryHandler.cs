using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.PAF.Contracts.Authorization;
using FSH.Modules.PAF.Contracts.v1.Parametros;
using FSH.Modules.PAF.Escopo;
using FSH.Modules.PAF.Parametros;
using Mediator;

namespace FSH.Modules.PAF.Features.v1.Parametros.ListParametros;

/// <summary>
/// RF06 screen read for the current unidade: effective value, origin and both stored rows of every catalog key, plus
/// what the caller may write, so the screen offers only the writes the API accepts.
/// </summary>
public sealed class ListParametrosQueryHandler(
    LeitorParametros leitor,
    IUnidadeCorrente unidadeCorrente,
    EscopoUsuario escopo,
    IUserPermissionService permissoes)
    : IQueryHandler<ListParametrosQuery, ParametrosDaUnidadeDto>
{
    public async ValueTask<ParametrosDaUnidadeDto> Handle(ListParametrosQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var unidadeId = unidadeCorrente.Exigir();
        var parametros = await leitor.CarregarAsync(unidadeId, cancellationToken).ConfigureAwait(false);
        var usuarioId = escopo.ObterUsuarioId().ToString();

        var alteraEmpresa = await permissoes.HasPermissionAsync(usuarioId, PafPermissions.Parametros.UpdateEmpresa, cancellationToken).ConfigureAwait(false)
            && await escopo.AdministraUnidadesAsync(cancellationToken).ConfigureAwait(false);
        var alteraUnidade = await permissoes.HasPermissionAsync(usuarioId, PafPermissions.Parametros.UpdateUnidade, cancellationToken).ConfigureAwait(false);

        var itens = CatalogoParametros.Todos
            .Select(p =>
            {
                var (valor, origem) = parametros.Efetivo(p);
                return new ParametroDto(p.Chave, p.Grupo, p.Tipo.ToString(), valor, origem.ToString(), p.Padrao,
                    parametros.ValorDaEmpresa(p.Chave), parametros.ValorDaUnidade(p.Chave), p.Opcoes);
            })
            .ToList();
        return new ParametrosDaUnidadeDto(unidadeId, alteraEmpresa, alteraUnidade, itens);
    }
}
