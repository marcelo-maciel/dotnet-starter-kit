using FSH.Framework.Core.Exceptions;
using FSH.Modules.PAF.Contracts.v1.Setores;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Escopo;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Setores.UpdateSetor;

public sealed class UpdateSetorCommandHandler(PafDbContext db, IUnidadeCorrente unidadeCorrente)
    : ICommandHandler<UpdateSetorCommand, Guid>
{
    public async ValueTask<Guid> Handle(UpdateSetorCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        unidadeCorrente.Exigir();

        var setor = await db.Setores
            .FirstOrDefaultAsync(s => s.Id == command.Id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException($"Setor {command.Id} não encontrado nesta unidade.");

        string nome = command.Nome.Trim();
        await NomeDoSetor.ExigirLivreAsync(db, nome, setor.Id, cancellationToken).ConfigureAwait(false);
        setor.Renomear(nome);
        await NomeDoSetor.SalvarAsync(db, cancellationToken).ConfigureAwait(false);
        return setor.Id;
    }
}
