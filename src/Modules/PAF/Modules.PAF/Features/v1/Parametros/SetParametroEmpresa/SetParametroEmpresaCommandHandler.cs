using FSH.Framework.Core.Exceptions;
using FSH.Modules.PAF.Contracts.v1.Parametros;
using FSH.Modules.PAF.Escopo;
using FSH.Modules.PAF.Parametros;
using Mediator;

namespace FSH.Modules.PAF.Features.v1.Parametros.SetParametroEmpresa;

/// <summary>
/// RF23: the empresa default belongs to the company admin alone. The permission is not enough: a filial admin holding
/// it would change the default of every other store, so the escopo empresa rule of the unidades cadastro applies too.
/// </summary>
public sealed class SetParametroEmpresaCommandHandler(GravacaoParametros gravacao, EscopoUsuario escopo)
    : ICommandHandler<SetParametroEmpresaCommand>
{
    public const string MensagemSoAdministradorDaEmpresa = "Só o administrador da empresa altera o padrão da empresa.";

    public async ValueTask<Unit> Handle(SetParametroEmpresaCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!await escopo.AdministraUnidadesAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new ForbiddenException(MensagemSoAdministradorDaEmpresa);
        }

        await gravacao.GravarAsync(unidadeId: null, command.Chave, command.Valor, cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
