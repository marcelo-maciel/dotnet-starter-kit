using FSH.Modules.PAF.Contracts.v1.Setores;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Escopo;
using Mediator;

namespace FSH.Modules.PAF.Features.v1.Setores.CreateSetor;

public sealed class CreateSetorCommandHandler(PafDbContext db, IUnidadeCorrente unidadeCorrente)
    : ICommandHandler<CreateSetorCommand, Guid>
{
    public async ValueTask<Guid> Handle(CreateSetorCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var unidadeId = unidadeCorrente.Exigir();
        string nome = command.Nome.Trim();

        await NomeDoSetor.ExigirLivreAsync(db, nome, null, cancellationToken).ConfigureAwait(false);
        var setor = Setor.Criar(unidadeId, nome);
        db.Setores.Add(setor);
        await NomeDoSetor.SalvarAsync(db, cancellationToken).ConfigureAwait(false);
        return setor.Id;
    }
}
