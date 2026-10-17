using System.Net;
using FSH.Framework.Core.Exceptions;
using FSH.Modules.PAF.Contracts.v1.Filas;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Escopo;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Filas.CreateFila;

public sealed class CreateFilaCommandHandler(PafDbContext db, IUnidadeCorrente unidadeCorrente)
    : ICommandHandler<CreateFilaCommand, Guid>
{
    public async ValueTask<Guid> Handle(CreateFilaCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var unidadeId = unidadeCorrente.Exigir();

        // Under the unit filter, a prioridade of another unidade simply does not exist here.
        var prioridadeIds = command.Prioridades.Select(p => p.PrioridadeId).ToList();
        int existentes = await db.Prioridades.AsNoTracking()
            .CountAsync(p => prioridadeIds.Contains(p.Id), cancellationToken)
            .ConfigureAwait(false);
        if (existentes != prioridadeIds.Count)
        {
            throw new CustomException("Prioridade inexistente nesta unidade.", (IEnumerable<string>?)null, HttpStatusCode.BadRequest);
        }

        var letras = command.Prioridades.Select(p => LetraDaSenha.Normalizar(p.Letra)).ToList();
        await LetraDaSenha.ExigirLivresAsync(db, letras, null, cancellationToken).ConfigureAwait(false);

        var fila = Fila.Criar(unidadeId, command.Nome.Trim(), command.NomeExibicao.Trim(), setorId: null);
        fila.DefinirJanela(command.HoraInicial, command.HoraFinal);
        db.Filas.Add(fila);
        db.FilaPrioridades.AddRange(command.Prioridades.Select(p => FilaPrioridade.Criar(
            unidadeId, fila.Id, p.PrioridadeId, LetraDaSenha.Normalizar(p.Letra), p.NumeroInicial, p.Ordem)));

        await LetraDaSenha.SalvarAsync(db, cancellationToken).ConfigureAwait(false);
        return fila.Id;
    }
}
