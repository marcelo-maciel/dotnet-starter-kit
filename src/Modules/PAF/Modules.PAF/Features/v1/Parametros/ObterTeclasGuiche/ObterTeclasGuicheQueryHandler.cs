using FSH.Modules.PAF.Contracts.v1.Parametros;
using FSH.Modules.PAF.Escopo;
using FSH.Modules.PAF.Parametros;
using Mediator;

namespace FSH.Modules.PAF.Features.v1.Parametros.ObterTeclasGuiche;

/// <summary>
/// The guichê's hotkeys under the guichê permission: the attendant holds no Parametros.View and still needs the keys
/// of the unidade he operates.
/// </summary>
public sealed class ObterTeclasGuicheQueryHandler(LeitorParametros leitor, IUnidadeCorrente unidadeCorrente)
    : IQueryHandler<ObterTeclasGuicheQuery, TeclasGuicheDto>
{
    public async ValueTask<TeclasGuicheDto> Handle(ObterTeclasGuicheQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var parametros = await leitor.CarregarAsync(unidadeCorrente.Exigir(), cancellationToken).ConfigureAwait(false);
        return new TeclasGuicheDto(
            parametros.Ler(CatalogoParametros.TeclaChamarProxima),
            parametros.Ler(CatalogoParametros.TeclaEncaminhar),
            parametros.Ler(CatalogoParametros.TeclaEncerrar),
            parametros.Ler(CatalogoParametros.TeclaEncerrarEChamar),
            parametros.Ler(CatalogoParametros.TeclaIniciar),
            parametros.Ler(CatalogoParametros.TeclaChamarNovamente));
    }
}
