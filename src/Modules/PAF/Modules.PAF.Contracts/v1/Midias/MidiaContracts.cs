using Mediator;

namespace FSH.Modules.PAF.Contracts.v1.Midias;

/// <summary>
/// Mídia of the current unidade (RF03). <paramref name="Tipo"/> is <c>Video</c>, <c>Imagem</c> or <c>Audio</c>;
/// <paramref name="Versao"/> is what an edit or a file swap sends back (409 when someone changed it in between).
/// </summary>
public sealed record MidiaDto(
    Guid Id,
    Guid UnidadeId,
    string Descricao,
    string Tipo,
    int DuracaoSegundos,
    bool Ativa,
    int Versao,
    DateTimeOffset CriadoEm);

/// <summary>
/// A presigned PUT: the browser sends the bytes straight to the store at <paramref name="Url"/> with every header of
/// <paramref name="CabecalhosObrigatorios"/> verbatim (the Content-Type is signed), before <paramref name="ExpiraEm"/>.
/// <paramref name="TetoBytes"/> is checked only at confirmation, because the signature does not bind the size.
/// </summary>
public sealed record EnvioMidiaDto(
    Guid EnvioId,
    Uri Url,
    IReadOnlyDictionary<string, string> CabecalhosObrigatorios,
    DateTimeOffset ExpiraEm,
    long TetoBytes);

/// <summary>
/// Asks for an upload in the current unidade. <paramref name="NomeArquivo"/> only picks the format (RF03 table, last
/// extension); it is never stored nor logged. <paramref name="ContentType"/> must be the format's MIME.
/// </summary>
public sealed record PedirEnvioMidiaCommand(string NomeArquivo, string ContentType) : ICommand<EnvioMidiaDto>, IEscritaDeCadastro;

/// <summary>
/// Upload for a file swap of mídia <paramref name="MidiaId"/>: same rules as <see cref="PedirEnvioMidiaCommand"/>,
/// under the update permission, and 404 when the mídia is not in the current unidade.
/// </summary>
public sealed record PedirEnvioTrocaMidiaCommand(Guid MidiaId, string NomeArquivo, string ContentType)
    : ICommand<EnvioMidiaDto>, IEscritaDeCadastro;

/// <summary>
/// Confirms envio <paramref name="EnvioId"/> against the store and creates the mídia. Idempotent: confirming the same
/// envio again returns the mídia already created.
/// </summary>
public sealed record CreateMidiaCommand(Guid EnvioId, string Descricao, int DuracaoSegundos) : ICommand<MidiaDto>, IEscritaDeCadastro;

/// <summary>Description and display duration; <paramref name="Versao"/> is the one the client read.</summary>
public sealed record UpdateMidiaCommand(Guid Id, string Descricao, int DuracaoSegundos, int Versao) : ICommand<MidiaDto>, IEscritaDeCadastro;

/// <summary>
/// Points the mídia to the file of envio <paramref name="EnvioId"/> (same type), after confirming it against the store.
/// The old object is never overwritten nor removed here: the key is never reused, and cleanup waits the 30-day lock.
/// </summary>
public sealed record TrocarArquivoMidiaCommand(Guid Id, Guid EnvioId, int Versao) : ICommand<MidiaDto>, IEscritaDeCadastro;

public sealed record EnableMidiaCommand(Guid Id) : ICommand<MidiaDto>, IEscritaDeCadastro;

public sealed record DisableMidiaCommand(Guid Id) : ICommand<MidiaDto>, IEscritaDeCadastro;

/// <summary>Mídias of the current unidade, by description. There is no deletion (RF03): only disable.</summary>
public sealed record ListMidiasQuery : IQuery<IReadOnlyList<MidiaDto>>;
