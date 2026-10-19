using FSH.Modules.PAF.Contracts.v1.Atendimentos;
using FSH.Modules.PAF.Contracts.v1.TempoReal;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Features.v1.Filas;
using FSH.Modules.PAF.Sessao;
using FSH.Modules.PAF.TempoReal;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Atendimentos.EncaminharAtendimento;

/// <summary>
/// RF17: the started atendimento goes to Encerrado and the same ficha gets one Aguardando atendimento per
/// destination fila, with the chosen prioridade and the origin recorded; no new number, the senha in the
/// client's hand stays valid. The destinations and the prioridade are re-validated under the locks, because the
/// screen is not access control.
/// </summary>
public sealed class EncaminharAtendimentoCommandHandler(PafDbContext db, SessaoCorrente sessao, ConcessaoCorrente concessao, AtosImpersonacao atos, AvisosTempoReal avisos)
    : ICommandHandler<EncaminharAtendimentoCommand, AtendimentoEncaminhadoDto>
{
    public const string MensagemSemAtendimentoIniciado = "Não há atendimento iniciado para ser encaminhado";
    public const string MensagemSemFilas = "Selecione a(s) fila(s) para qual deseja encaminhar a ficha";
    public const string MensagemDestinoIndisponivel = "Fila de destino indisponível: escolha entre as filas ativas da unidade, exceto a de origem.";
    public const string MensagemPrioridadeForaDaIntersecao =
        "A prioridade escolhida não está associada, com guichê que atenda, a todas as filas de destino.";

    public async ValueTask<AtendimentoEncaminhadoDto> Handle(EncaminharAtendimentoCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var guiche = await OperacaoGuiche.ExigirAsync(db, DonoPosto.Exigir(sessao, concessao), cancellationToken);

        await using var transacao = await db.Database.BeginTransactionAsync(cancellationToken);

        // First, before any row lock: DisableEstacao holds this lock exclusively and then writes SessoesEstacao, so
        // taking it after the link lock would close a wait cycle. Held shared until commit, no station write that
        // removes coverage commits between the check below and this commit (same rule as the emission).
        await CoberturaGuiche.TravarParaEmissaoAsync(db, guiche.TenantId, guiche.Estacao.UnidadeId, cancellationToken);

        var estacaoDoVinculo = await VinculosGuiche.TravarAtivoDoDonoAsync(db, guiche.Dono, cancellationToken);
        if (estacaoDoVinculo != guiche.Estacao.Id)
        {
            throw OperacaoGuiche.Conflito(MensagensGuiche.MensagemSemGuiche);
        }

        var origem = await OperacaoGuiche.TravarAsync(db, atos, guiche, command.AtendimentoId, cancellationToken);
        OperacaoGuiche.ExigirEstado(origem, guiche, EstadoAtendimento.EmAtendimento, MensagemSemAtendimentoIniciado);
        await ExigirDestinosAsync(origem, command, cancellationToken);

        var versao = await OperacaoGuiche.TransitarAsync(db, origem, $"""
            UPDATE paf."Atendimentos"
               SET "Estado" = 'Encerrado', "EncerradoEm" = now(), "Versao" = "Versao" + 1
             WHERE "TenantId" = {guiche.TenantId} AND "Id" = {origem.Id} AND "Estado" = 'EmAtendimento'
            RETURNING "Versao" AS "Value"
            """, cancellationToken);

        // Before the inserts, so the release never touches the new rows; their ElegivelDesde is now() anyway.
        await OperacaoGuiche.LiberarPendentesAsync(db, guiche, origem, cancellationToken);

        var destinos = command.FilaIds
            .Select(filaId => Atendimento.Criar(origem.UnidadeId, origem.FichaId, filaId, command.PrioridadeId, origem.Id))
            .ToList();
        db.Atendimentos.AddRange(destinos);
        await db.SaveChangesAsync(cancellationToken);
        await OperacaoGuiche.SoltarVinculoDeEstacaoInativaAsync(db, guiche.TenantId, guiche.Estacao.Id, cancellationToken);

        var correlacao = OperacaoGuiche.Correlacao();
        var aviso = OperacaoGuiche.AvisoDeEstado(guiche, origem, versao, EstadoAtendimento.Encerrado);
        var filas = command.FilaIds
            .Select(filaId => FilaAtualizadaPaf.Criar(guiche.TenantId, correlacao, new FilaAtualizada(Guid.CreateVersion7(), origem.UnidadeId, filaId)))
            .ToList();

        await avisos.GravarAsync(aviso, cancellationToken);
        foreach (var fila in filas)
        {
            await avisos.GravarAsync(fila, cancellationToken);
        }

        await transacao.CommitAsync(cancellationToken);
        await avisos.EnviarAsync(aviso, cancellationToken);
        foreach (var fila in filas)
        {
            await avisos.EnviarAsync(fila, cancellationToken);
        }

        return new AtendimentoEncaminhadoDto(origem.Id, versao, destinos.Select(d => new AtendimentoDestinoDto(d.Id, d.FilaId)).ToList());
    }

    /// <summary>
    /// Every destination is an active fila of the current unit (unit filter) other than the origin, and the
    /// prioridade is active and associated to each one with guichê coverage (RF17, decided 03/10/2026). An inactive
    /// prioridade is refused like an inactive fila: its fichas already waiting stay callable, but nothing new enters
    /// it. The fila window is not checked: forwarding is not emission.
    /// </summary>
    private async Task ExigirDestinosAsync(AtendimentoTravado origem, EncaminharAtendimentoCommand command, CancellationToken ct)
    {
        var filaIds = command.FilaIds;
        var validas = await db.Filas.AsNoTracking()
            .CountAsync(f => filaIds.Contains(f.Id) && f.Ativa && f.Id != origem.FilaId, ct);
        if (validas != filaIds.Count)
        {
            throw OperacaoGuiche.Conflito(MensagemDestinoIndisponivel);
        }

        var pares = CoberturaGuiche.Pares(db);
        var comPrioridade = await (
                from fp in db.FilaPrioridades.AsNoTracking()
                join p in db.Prioridades.AsNoTracking() on fp.PrioridadeId equals p.Id
                where filaIds.Contains(fp.FilaId) && fp.PrioridadeId == command.PrioridadeId && p.Ativa
                where pares.Any(c => c.FilaId == fp.FilaId && c.PrioridadeId == fp.PrioridadeId)
                select fp.FilaId)
            .Distinct()
            .CountAsync(ct);
        if (comPrioridade != filaIds.Count)
        {
            throw OperacaoGuiche.Conflito(MensagemPrioridadeForaDaIntersecao);
        }
    }
}
