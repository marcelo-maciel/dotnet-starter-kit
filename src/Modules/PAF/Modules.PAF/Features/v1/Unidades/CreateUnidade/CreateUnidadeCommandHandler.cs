using FSH.Modules.PAF.Contracts.v1.Unidades;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Escopo;
using Mediator;

namespace FSH.Modules.PAF.Features.v1.Unidades.CreateUnidade;

/// <summary>
/// Only the escopo empresa profile creates (RF23), and that scope already covers every unidade of the tenant,
/// so the new unidade reaches its creator with no scope row written here.
/// </summary>
public sealed class CreateUnidadeCommandHandler(PafDbContext db, EscopoUsuario escopo)
    : ICommandHandler<CreateUnidadeCommand, Guid>
{
    public async ValueTask<Guid> Handle(CreateUnidadeCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await escopo.ExigirAdministracaoDeUnidadesAsync(cancellationToken).ConfigureAwait(false);

        var unidade = Unidade.Criar(command.Nome.Trim(), command.DocumentoFiscal?.Trim());
        db.Unidades.Add(unidade);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return unidade.Id;
    }
}
