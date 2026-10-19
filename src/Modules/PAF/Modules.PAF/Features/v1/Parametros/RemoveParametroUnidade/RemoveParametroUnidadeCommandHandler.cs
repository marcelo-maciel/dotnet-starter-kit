using FSH.Modules.PAF.Contracts.v1.Parametros;
using FSH.Modules.PAF.Escopo;
using FSH.Modules.PAF.Parametros;
using Mediator;

namespace FSH.Modules.PAF.Features.v1.Parametros.RemoveParametroUnidade;

public sealed class RemoveParametroUnidadeCommandHandler(GravacaoParametros gravacao, IUnidadeCorrente unidadeCorrente)
    : ICommandHandler<RemoveParametroUnidadeCommand>
{
    public async ValueTask<Unit> Handle(RemoveParametroUnidadeCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await gravacao.RemoverDaUnidadeAsync(unidadeCorrente.Exigir(), command.Chave, cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
