using FluentValidation;
using FSH.Modules.PAF.Contracts.v1.Midias;

namespace FSH.Modules.PAF.Features.v1.Midias.PedirEnvioMidia;

public sealed class PedirEnvioMidiaCommandValidator : AbstractValidator<PedirEnvioMidiaCommand>
{
    public PedirEnvioMidiaCommandValidator()
    {
        RuleFor(x => x.NomeArquivo).NomeDeArquivoAceito();

        // Only judged once the extension is known: an unknown format already answered with the list.
        RuleFor(x => x.ContentType).NotEmpty()
            .Must((x, mime) => RegrasMidia.MimeConfere(x.NomeArquivo, mime)).WithMessage(_ => RegrasMidia.MensagemMime)
            .When(x => RegrasMidia.FormatoConhecido(x.NomeArquivo));
    }
}

public sealed class PedirEnvioTrocaMidiaCommandValidator : AbstractValidator<PedirEnvioTrocaMidiaCommand>
{
    public PedirEnvioTrocaMidiaCommandValidator()
    {
        RuleFor(x => x.MidiaId).NotEmpty();
        RuleFor(x => x.NomeArquivo).NomeDeArquivoAceito();
        RuleFor(x => x.ContentType).NotEmpty()
            .Must((x, mime) => RegrasMidia.MimeConfere(x.NomeArquivo, mime)).WithMessage(_ => RegrasMidia.MensagemMime)
            .When(x => RegrasMidia.FormatoConhecido(x.NomeArquivo));
    }
}
