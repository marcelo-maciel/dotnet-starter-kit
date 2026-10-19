using FluentValidation;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Midias;

namespace FSH.Modules.PAF.Features.v1.Midias;

/// <summary>Request rules of the mídia cadastro (RF03, RNF11), shared by the validators.</summary>
internal static class RegrasMidia
{
    public const int NomeArquivoMaxLength = 255;

    public static string MensagemFormato => $"Formato não aceito. Formatos aceitos: {FormatosMidia.ListaAceitos}.";

    public static string MensagemMime => $"O tipo do arquivo (MIME) não corresponde à extensão. Formatos aceitos: {FormatosMidia.ListaAceitos}.";

    public static bool FormatoConhecido(string? nomeArquivo) =>
        nomeArquivo is not null && FormatosMidia.PorNomeDeArquivo(nomeArquivo) is not null;

    public static bool MimeConfere(string? nomeArquivo, string? mime) =>
        mime is not null
        && nomeArquivo is not null
        && FormatosMidia.PorNomeDeArquivo(nomeArquivo) is { } formato
        && string.Equals(ArmazenamentoMidia.TipoSemParametros(mime), formato.Mime, StringComparison.OrdinalIgnoreCase);

    public static IRuleBuilderOptions<T, string> NomeDeArquivoAceito<T>(this IRuleBuilder<T, string> regra) =>
        regra.NotEmpty()
            .MaximumLength(NomeArquivoMaxLength)
            .Must(FormatoConhecido).WithMessage(_ => MensagemFormato);

    public static IRuleBuilderOptions<T, string> DescricaoDaMidia<T>(this IRuleBuilder<T, string> regra) =>
        regra.Must(d => !string.IsNullOrWhiteSpace(d)).WithMessage("A descrição é obrigatória.")
            .Must(d => d is null || d.Trim().Length <= Midia.DescricaoMaxLength)
            .WithMessage($"A descrição tem no máximo {Midia.DescricaoMaxLength} caracteres.");

    public static IRuleBuilderOptions<T, int> DuracaoDaMidia<T>(this IRuleBuilder<T, int> regra) =>
        regra.InclusiveBetween(1, Midia.DuracaoMaximaSegundos)
            .WithMessage($"A duração da exibição vai de 1 a {Midia.DuracaoMaximaSegundos} segundos.");
}
