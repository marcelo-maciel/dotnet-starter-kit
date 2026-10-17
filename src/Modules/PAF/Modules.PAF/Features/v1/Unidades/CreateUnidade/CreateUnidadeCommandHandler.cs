using FSH.Modules.PAF.Contracts.v1.Unidades;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Escopo;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Unidades.CreateUnidade;

public sealed class CreateUnidadeCommandHandler(PafDbContext db, EscopoUsuario escopo)
    : ICommandHandler<CreateUnidadeCommand, Guid>
{
    public async ValueTask<Guid> Handle(CreateUnidadeCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var unidade = Unidade.Criar(command.Nome.Trim(), command.DocumentoFiscal?.Trim());
        db.Unidades.Add(unidade);

        // ponytail: the creator gets the new unidade at the end of their scope, because the empresa scope
        // of RF08 (every active unidade without listing them) is not modelled yet; without this nobody could
        // operate or grant a freshly created unidade. Drop it when the empresa scope lands.
        var usuarioId = escopo.ObterUsuarioId();
        int? ultimaOrdem = await db.UsuariosUnidade
            .Where(u => u.UsuarioId == usuarioId)
            .MaxAsync(u => (int?)u.Ordem, cancellationToken)
            .ConfigureAwait(false);
        db.UsuariosUnidade.Add(UsuarioUnidade.Criar(usuarioId, unidade.Id, (ultimaOrdem ?? -1) + 1));

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return unidade.Id;
    }
}
