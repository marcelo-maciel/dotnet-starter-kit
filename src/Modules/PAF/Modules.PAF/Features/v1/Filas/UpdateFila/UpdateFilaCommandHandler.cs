using FSH.Framework.Core.Exceptions;
using FSH.Modules.PAF.Contracts.v1.Filas;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Escopo;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Filas.UpdateFila;

public sealed class UpdateFilaCommandHandler(PafDbContext db, IUnidadeCorrente unidadeCorrente)
    : ICommandHandler<UpdateFilaCommand, Guid>
{
    public async ValueTask<Guid> Handle(UpdateFilaCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        unidadeCorrente.Exigir();

        var fila = await db.Filas
            .FirstOrDefaultAsync(f => f.Id == command.Id, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NotFoundException($"Fila {command.Id} não encontrada.");

        await AparenciaDaFila.ExigirSetorAsync(db, command.SetorId, cancellationToken).ConfigureAwait(false);
        var aparencia = command.Aparencia;
        var grade = await AparenciaDaFila.CoresDaGradeAsync(db, fila.Id, null, cancellationToken).ConfigureAwait(false);
        AparenciaDaFila.ExigirContraste(aparencia.CorFonte, grade.Prepend(("fila selecionada", aparencia.CorBotaoSelecionado)));

        fila.Atualizar(command.Nome.Trim(), command.NomeExibicao.Trim());
        fila.DefinirJanela(command.HoraInicial, command.HoraFinal);
        fila.DefinirSetor(command.SetorId);
        fila.DefinirAparencia(aparencia.Fonte?.Trim(), aparencia.CorFonte, aparencia.CorBotaoSelecionado);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return fila.Id;
    }
}
