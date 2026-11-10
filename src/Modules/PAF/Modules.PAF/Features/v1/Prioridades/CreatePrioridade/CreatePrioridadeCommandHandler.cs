using FSH.Modules.PAF.Contracts.v1.Prioridades;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Escopo;
using Mediator;

namespace FSH.Modules.PAF.Features.v1.Prioridades.CreatePrioridade;

public sealed class CreatePrioridadeCommandHandler(PafDbContext db, IUnidadeCorrente unidadeCorrente)
    : ICommandHandler<CreatePrioridadeCommand, Guid>
{
    public async ValueTask<Guid> Handle(CreatePrioridadeCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var prioridade = Prioridade.Criar(unidadeCorrente.Exigir(), command.Nome.Trim(), command.Valor);
        db.Prioridades.Add(prioridade);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return prioridade.Id;
    }
}
