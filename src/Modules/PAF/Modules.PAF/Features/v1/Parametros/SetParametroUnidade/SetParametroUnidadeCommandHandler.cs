using FSH.Modules.PAF.Contracts.v1.Parametros;
using FSH.Modules.PAF.Escopo;
using FSH.Modules.PAF.Parametros;
using Mediator;

namespace FSH.Modules.PAF.Features.v1.Parametros.SetParametroUnidade;

/// <summary>The unidade comes from the current unit, never from the request: a caller only overrides where he operates.</summary>
public sealed class SetParametroUnidadeCommandHandler(GravacaoParametros gravacao, IUnidadeCorrente unidadeCorrente)
    : ICommandHandler<SetParametroUnidadeCommand>
{
    public async ValueTask<Unit> Handle(SetParametroUnidadeCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await gravacao.GravarAsync(unidadeCorrente.Exigir(), command.Chave, command.Valor, cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
