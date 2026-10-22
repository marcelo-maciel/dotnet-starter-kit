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
        var prioridades = await db.Prioridades.AsNoTracking()
            .Where(p => command.Prioridades.Select(i => i.PrioridadeId).Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Nome, cancellationToken)
            .ConfigureAwait(false);
        if (prioridades.Count != command.Prioridades.Count)
        {
            throw new CustomException("Prioridade inexistente nesta unidade.", (IEnumerable<string>?)null, HttpStatusCode.BadRequest);
        }

        await AparenciaDaFila.ExigirSetorAsync(db, command.SetorId, cancellationToken).ConfigureAwait(false);
        var aparencia = command.Aparencia;
        AparenciaDaFila.ExigirContraste(
            aparencia.CorFonte,
            command.Prioridades.Select(i => (prioridades[i.PrioridadeId], i.CorBotao))
                .Prepend(("fila selecionada", aparencia.CorBotaoSelecionado)));

        var letras = command.Prioridades.Select(p => LetraDaSenha.Normalizar(p.Letra)).ToList();
        await LetraDaSenha.ExigirLivresAsync(db, letras, null, cancellationToken).ConfigureAwait(false);

        var fila = Fila.Criar(unidadeId, command.Nome.Trim(), command.NomeExibicao.Trim(), command.SetorId);
        fila.DefinirJanela(command.HoraInicial, command.HoraFinal);
        fila.DefinirAparencia(aparencia.Fonte?.Trim(), aparencia.CorFonte, aparencia.CorBotaoSelecionado);
        db.Filas.Add(fila);
        foreach (var i in command.Prioridades)
        {
            var item = FilaPrioridade.Criar(unidadeId, fila.Id, i.PrioridadeId, LetraDaSenha.Normalizar(i.Letra), i.NumeroInicial, i.Ordem);
            item.DefinirCorBotao(i.CorBotao);
            db.FilaPrioridades.Add(item);
        }

        await LetraDaSenha.SalvarAsync(db, cancellationToken).ConfigureAwait(false);
        return fila.Id;
    }
}
