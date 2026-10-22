namespace FSH.Modules.PAF.Contracts.v1.TempoReal;

/// <summary>Route of the PAF SignalR hub; the client also sends <c>?tenant=</c> and <c>?access_token=</c>.</summary>
public static class PafHubRota
{
    public const string Caminho = "/api/v1/paf/hub";
}

/// <summary>
/// Server-to-client method names of the PAF hub. Every payload carries <c>EventoId</c>: each event
/// arrives twice by design (direct send and outbox), and the screen drops the id it already applied.
/// </summary>
public static class PafHubMetodos
{
    public const string SenhaChamada = "SenhaChamada";
    public const string SenhaRechamada = "SenhaRechamada";
    public const string AtendimentoAtualizado = "AtendimentoAtualizado";
    public const string FilaAtualizada = "FilaAtualizada";
}

/// <summary>Client-to-server method names of the PAF hub; invoke only while the connection is Connected.</summary>
public static class PafHubChamadas
{
    /// <summary>Guichê screen, after selecting a guichê and after every reconnect; returns the guichê id or null.</summary>
    public const string EntrarNoGuiche = "EntrarNoGuiche";
}

/// <summary>
/// A ficha was called by a guichê. <paramref name="Versao"/> is the atendimento version after the
/// call; a screen ignores any event whose version is lower than the one it already applied (seção 7).
/// </summary>
public sealed record SenhaChamada(
    Guid EventoId,
    Guid UnidadeId,
    Guid AtendimentoId,
    Guid FichaId,
    int Versao,
    string Senha,
    Guid EstacaoId,
    string NomeEstacao)
{
    /// <summary>Guichê number of the RF04 block, announced by the painel; null for a station saved before C04.</summary>
    public int? NumeroGuiche { get; init; }

    /// <summary>Display name of the RF04 guichê block; null when not set.</summary>
    public string? NomeExibicaoGuiche { get; init; }
}

/// <summary>Same ficha announced again (RF21): the painel moves the existing entry to the top, never duplicates it.</summary>
public sealed record SenhaRechamada(
    Guid EventoId,
    Guid UnidadeId,
    Guid AtendimentoId,
    Guid FichaId,
    int Versao,
    string Senha,
    Guid EstacaoId,
    string NomeEstacao)
{
    /// <summary>Guichê number of the RF04 block, announced by the painel; null for a station saved before C04.</summary>
    public int? NumeroGuiche { get; init; }

    /// <summary>Display name of the RF04 guichê block; null when not set.</summary>
    public string? NomeExibicaoGuiche { get; init; }
}

/// <summary>
/// Any other state change of an atendimento (iniciado, encerrado, cancelado, encaminhado, reabilitado).
/// <paramref name="Estado"/> is the state name after the change, as persisted.
/// </summary>
public sealed record AtendimentoAtualizado(
    Guid EventoId,
    Guid UnidadeId,
    Guid AtendimentoId,
    Guid FichaId,
    int Versao,
    string Estado,
    Guid? EstacaoId);

/// <summary>The queue of a fila changed (ficha emitida, chamada, liberada); the screen re-queries it.</summary>
public sealed record FilaAtualizada(
    Guid EventoId,
    Guid UnidadeId,
    Guid FilaId);
