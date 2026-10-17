using FSH.Modules.PAF.Contracts.v1.Unidades;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using Mediator;

namespace FSH.Modules.PAF.Features.v1.Unidades.CreateUnidade;

/// <summary>
/// The creator does not join the new unidade's scope: a scope row would make him a licensed PAF user in the
/// middle of a session he opened without a licence, refused on the next request.
/// </summary>
public sealed class CreateUnidadeCommandHandler(PafDbContext db)
    : ICommandHandler<CreateUnidadeCommand, Guid>
{
    public async ValueTask<Guid> Handle(CreateUnidadeCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var unidade = Unidade.Criar(command.Nome.Trim(), command.DocumentoFiscal?.Trim());
        db.Unidades.Add(unidade);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return unidade.Id;
    }
}
