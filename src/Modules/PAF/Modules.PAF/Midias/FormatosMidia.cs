namespace FSH.Modules.PAF.Midias;

/// <summary>Tipo da mídia do RF03: o que o painel toca e qual teto de tamanho vale para o envio.</summary>
// Persisted as strings: CK_Midias_Tipo and CK_EnviosMidia_Tipo list the literal names.
public enum TipoMidia
{
    Video,
    Imagem,
    Audio,
}

/// <summary>Um formato aceito: a extensão como entra na chave do objeto, o tipo e o MIME assinado no envio.</summary>
public sealed record FormatoMidia(string Extensao, TipoMidia Tipo, string Mime);

/// <summary>
/// Tabela única dos formatos do RF03 (requisitos, "RF03 - Cadastro de arquivos"): só os de suporte universal nos
/// navegadores. Nenhum outro ponto do módulo declara extensão ou MIME de mídia.
/// </summary>
public static class FormatosMidia
{
    // MIME as mapped by ASP.NET Core's FileExtensionContentTypeProvider (10.0.13), which the kit's storage already
    // uses: one source for what the store will be told, not a list recalled from memory.
    public static readonly IReadOnlyList<FormatoMidia> Aceitos =
    [
        new(".mp4", TipoMidia.Video, "video/mp4"),
        new(".webm", TipoMidia.Video, "video/webm"),
        new(".jpg", TipoMidia.Imagem, "image/jpeg"),
        new(".jpeg", TipoMidia.Imagem, "image/jpeg"),
        new(".png", TipoMidia.Imagem, "image/png"),
        new(".webp", TipoMidia.Imagem, "image/webp"),
        new(".gif", TipoMidia.Imagem, "image/gif"),
        new(".mp3", TipoMidia.Audio, "audio/mpeg"),
        new(".m4a", TipoMidia.Audio, "audio/mp4"),
    ];

    /// <summary>Lista legível dos formatos aceitos, para a mensagem de recusa do RF03.</summary>
    public static string ListaAceitos => string.Join(", ", Aceitos.Select(f => f.Extensao));

    /// <summary>
    /// O formato da extensão do nome enviado, sem diferença entre maiúsculas e minúsculas (<c>.MP4</c> é
    /// <c>.mp4</c>), ou nulo quando ela não está na tabela. A comparação é ordinal contra a tabela, então espaço,
    /// acento, emoji ou separador de caminho na extensão nunca casam.
    /// </summary>
    public static FormatoMidia? PorNomeDeArquivo(string nomeArquivo)
    {
        ArgumentNullException.ThrowIfNull(nomeArquivo);

        var ponto = nomeArquivo.LastIndexOf('.');
        if (ponto < 0)
        {
            return null;
        }

        var extensao = nomeArquivo[ponto..];
        return Aceitos.FirstOrDefault(f => string.Equals(f.Extensao, extensao, StringComparison.OrdinalIgnoreCase));
    }
}
