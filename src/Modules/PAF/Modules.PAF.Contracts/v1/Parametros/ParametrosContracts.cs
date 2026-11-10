using Mediator;

namespace FSH.Modules.PAF.Contracts.v1.Parametros;

/// <summary>RF06 screen: every catalog key with its effective value for the current unidade and where it came from.</summary>
public sealed record ListParametrosQuery : IQuery<ParametrosDaUnidadeDto>;

/// <summary>
/// <paramref name="AlteraEmpresa"/>: the caller may write the empresa default (RF23: company admin only);
/// <paramref name="AlteraUnidade"/>: the caller may override in the current unidade.
/// </summary>
public sealed record ParametrosDaUnidadeDto(Guid UnidadeId, bool AlteraEmpresa, bool AlteraUnidade, IReadOnlyList<ParametroDto> Itens);

/// <summary>
/// One key. <paramref name="Origem"/> is <c>Fabrica</c>, <c>Empresa</c> or <c>Unidade</c>; <paramref name="ValorEmpresa"/>
/// and <paramref name="ValorUnidade"/> are the stored rows (null = no row), so the screen shows what an override hides.
/// </summary>
public sealed record ParametroDto(
    string Chave,
    string Grupo,
    string Tipo,
    string Valor,
    string Origem,
    string Padrao,
    string? ValorEmpresa,
    string? ValorUnidade,
    IReadOnlyList<string> Opcoes);

/// <summary>Body of the two PUTs; the key comes from the route.</summary>
public sealed record ParametroValorRequest(string Valor);

/// <summary>Writes the empresa default (UnidadeId NULL) of a key.</summary>
public sealed record SetParametroEmpresaCommand(string Chave, string Valor) : ICommand, IEscritaDeCadastro;

/// <summary>Overrides a key in the current unidade.</summary>
public sealed record SetParametroUnidadeCommand(string Chave, string Valor) : ICommand, IEscritaDeCadastro;

/// <summary>Drops the current unidade's override: the key falls back to the empresa default, else the factory one.</summary>
public sealed record RemoveParametroUnidadeCommand(string Chave) : ICommand, IEscritaDeCadastro;

/// <summary>The guichê's hotkeys for its current unidade, readable by whoever operates a guichê.</summary>
public sealed record ObterTeclasGuicheQuery : IQuery<TeclasGuicheDto>;

/// <summary>One key name (F1 to F12) per guichê action of RF06.</summary>
public sealed record TeclasGuicheDto(
    string ChamarProxima,
    string Encaminhar,
    string Encerrar,
    string EncerrarEChamar,
    string Iniciar,
    string ChamarNovamente);
