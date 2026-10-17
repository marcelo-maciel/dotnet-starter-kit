using FSH.Modules.PAF.Contracts.v1.Estacoes;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Escopo;
using Mediator;

namespace FSH.Modules.PAF.Features.v1.Estacoes.CreateEstacao;

public sealed class CreateEstacaoCommandHandler(PafDbContext db, IUnidadeCorrente unidadeCorrente)
    : ICommandHandler<CreateEstacaoCommand, Guid>
{
    public async ValueTask<Guid> Handle(CreateEstacaoCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var unidadeId = unidadeCorrente.Exigir();
        var tipo = Enum.Parse<TipoEstacao>(command.Tipo);

        await ListasDaEstacao.ValidarAsync(db, tipo, command.Filas, command.Prioridades, cancellationToken).ConfigureAwait(false);

        var estacao = Estacao.Criar(unidadeId, command.Nome.Trim(), tipo);
        db.Estacoes.Add(estacao);
        await ListasDaEstacao.SincronizarAsync(db, estacao, command.Filas, command.Prioridades, cancellationToken).ConfigureAwait(false);

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return estacao.Id;
    }
}
