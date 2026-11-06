using FluentValidation;
using FSH.Modules.PAF.Contracts.v1.Midias;

namespace FSH.Modules.PAF.Features.v1.Midias.TrocarArquivoMidia;

public sealed class TrocarArquivoMidiaCommandValidator : AbstractValidator<TrocarArquivoMidiaCommand>
{
    public TrocarArquivoMidiaCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.EnvioId).NotEmpty();
        RuleFor(x => x.Versao).GreaterThanOrEqualTo(0);
    }
}
