using FSH.Framework.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FSH.Modules.PAF.Midias;

/// <summary>
/// Opções da mídia do RF03 em <c>PAF:Midia</c>, validadas na inicialização: um valor fora da faixa, ou um
/// <c>Storage:S3:Prefix</c> que tiraria a chave da mídia do lugar, derruba o host em vez de gravar objeto onde o
/// bloqueio de 30 dias e as políticas de bucket não esperam.
/// </summary>
// ponytail: one value per host; per-tenant ceilings, if a client contract ever asks for them, become RF06 parameters.
public sealed class OpcoesMidia
{
    public const string Secao = "PAF:Midia";

    /// <summary>Teto de validade de URL assinada aceito pelo R2 (ADR 0007).</summary>
    public static readonly TimeSpan ValidadeMaximaAssinatura = TimeSpan.FromDays(7);

    /// <summary>Validade da URL assinada de envio (PUT direto do navegador ao armazenamento).</summary>
    public TimeSpan ValidadeUrlEnvio { get; set; } = TimeSpan.FromMinutes(15);

    public long TetoVideoBytes { get; set; } = 500L * 1024 * 1024;

    public long TetoImagemBytes { get; set; } = 20L * 1024 * 1024;

    public long TetoAudioBytes { get; set; } = 50L * 1024 * 1024;

    /// <summary>
    /// Folga somada à duração da exibição na validade da URL de leitura, para a mídia não expirar no meio do laço
    /// (ADR 0007); a validade nunca passa de <see cref="ValidadeMaximaAssinatura"/>.
    /// </summary>
    public TimeSpan MargemLeitura { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Validade mínima da URL de leitura (M04): uma imagem de 10 s não ganha uma URL de 10 min que o painel teria de
    /// pedir de novo a cada poucos laços, e cada URL nova faz o navegador baixar o arquivo outra vez.
    /// </summary>
    public TimeSpan PisoValidadeUrlLeitura { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Prazo de cada chamada do cadastro ao armazenamento (RNF05): com o armazenamento parado, o pedido responde 503
    /// dentro dele em vez de segurar a requisição pelo prazo do cliente S3.
    /// </summary>
    public TimeSpan PrazoArmazenamento { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Same ceiling as the RNF05 barrier of the PAF tests (<c>RegrasDePrazo.Teto</c>).</summary>
    public static readonly TimeSpan PrazoArmazenamentoMaximo = TimeSpan.FromSeconds(30);

    public long TetoBytes(TipoMidia tipo) => tipo switch
    {
        TipoMidia.Video => TetoVideoBytes,
        TipoMidia.Imagem => TetoImagemBytes,
        TipoMidia.Audio => TetoAudioBytes,
        _ => throw new ArgumentOutOfRangeException(nameof(tipo), tipo, "Tipo de mídia sem teto."),
    };

    public static void Registrar(IServiceCollection services)
    {
        services.AddOptions<OpcoesMidia>()
            .BindConfiguration(Secao)
            .Validate(o => o.ValidadeUrlEnvio > TimeSpan.Zero && o.ValidadeUrlEnvio <= ValidadeMaximaAssinatura,
                "PAF:Midia:ValidadeUrlEnvio precisa ser positiva e no máximo 7 dias.")
            .Validate(o => o.TetoVideoBytes > 0 && o.TetoImagemBytes > 0 && o.TetoAudioBytes > 0,
                "PAF:Midia: os tetos por tipo precisam ser positivos.")
            .Validate(o => o.MargemLeitura > TimeSpan.Zero && o.MargemLeitura < ValidadeMaximaAssinatura,
                "PAF:Midia:MargemLeitura precisa ser positiva e menor que 7 dias.")
            .Validate(o => o.PisoValidadeUrlLeitura > TimeSpan.Zero && o.PisoValidadeUrlLeitura <= ValidadeMaximaAssinatura,
                "PAF:Midia:PisoValidadeUrlLeitura precisa ser positivo e no máximo 7 dias.")
            .Validate(o => o.PrazoArmazenamento > TimeSpan.Zero && o.PrazoArmazenamento <= PrazoArmazenamentoMaximo,
                "PAF:Midia:PrazoArmazenamento precisa ser positivo e no máximo 30 segundos.")
            .Validate<IConfiguration>((_, configuracao) => PrefixoDoKitCompativel(configuracao["Storage:S3:Prefix"]),
                "Storage:S3:Prefix não pode começar por public/ nem colidir com a raiz paf-midia/ da mídia do PAF.")
            .ValidateOnStart();
    }

    /// <summary>
    /// O provedor S3 do kit põe o prefixo na frente da chave, salvo quando a chave já começa pelo texto do prefixo
    /// (comparação sem âncora em <c>S3StorageService.NormalizeKey</c>). Um prefixo como <c>paf</c> deixaria a mídia
    /// sem prefixo, e um prefixo sob <c>public/</c> a deixaria legível sem assinatura.
    /// </summary>
    public static bool PrefixoDoKitCompativel(string? prefixo)
    {
        if (string.IsNullOrWhiteSpace(prefixo))
        {
            return true;
        }

        var limpo = prefixo.Trim('/');
        if (limpo.Length == 0)
        {
            return true;
        }

        var colideComARaiz = ChaveObjetoMidia.Raiz.StartsWith(prefixo, StringComparison.OrdinalIgnoreCase)
            || limpo.StartsWith(ChaveObjetoMidia.Raiz.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
        var publico = (limpo + "/").StartsWith(StorageVisibilityRoot.Public, StringComparison.OrdinalIgnoreCase);
        return !colideComARaiz && !publico;
    }
}
