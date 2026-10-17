using FSH.Modules.PAF.Contracts.v1.Atendimentos;
using FSH.Modules.PAF.Contracts.v1.TempoReal;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Sessao;
using FSH.Modules.PAF.TempoReal;
using Mediator;

namespace FSH.Modules.PAF.Features.v1.Atendimentos.RechamarAtendimento;

/// <summary>
/// Announces the called ficha again (RF21). ChamadoEm is never rewritten; the recall gets its own Rechamada row
/// and UltimaRechamadaEm, which the painel uses to reorder its list on reconnect. A recall that waited on a
/// cancellation finds the atendimento cancelled and announces nothing (seção 4).
/// </summary>
public sealed class RechamarAtendimentoCommandHandler(PafDbContext db, SessaoCorrente sessao, AvisosTempoReal avisos)
    : ICommandHandler<RechamarAtendimentoCommand, AtendimentoAlteradoDto>
{
    public async ValueTask<AtendimentoAlteradoDto> Handle(RechamarAtendimentoCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var guiche = await OperacaoGuiche.ExigirAsync(db, sessao, cancellationToken);

        await using var transacao = await db.Database.BeginTransactionAsync(cancellationToken);
        var atendimento = await OperacaoGuiche.TravarAsync(db, guiche, command.AtendimentoId, cancellationToken);
        OperacaoGuiche.ExigirEstado(atendimento, guiche, EstadoAtendimento.Chamado, MensagensGuiche.MensagemNaoChamado);

        db.Rechamadas.Add(Rechamada.Criar(atendimento.UnidadeId, atendimento.Id, guiche.Estacao.Id, guiche.UsuarioId));
        await db.SaveChangesAsync(cancellationToken);
        var versao = await OperacaoGuiche.TransitarAsync(db, atendimento, $"""
            UPDATE paf."Atendimentos"
               SET "UltimaRechamadaEm" = now(), "Versao" = "Versao" + 1
             WHERE "TenantId" = {guiche.TenantId} AND "Id" = {atendimento.Id} AND "Estado" = 'Chamado'
            RETURNING "Versao" AS "Value"
            """, cancellationToken);

        var paineis = await OperacaoGuiche.PaineisDaFilaAsync(db, atendimento.FilaId, cancellationToken);
        var aviso = SenhaRechamadaPaf.Criar(guiche.TenantId, OperacaoGuiche.Correlacao(), new SenhaRechamada(
            Guid.CreateVersion7(), atendimento.UnidadeId, atendimento.Id, atendimento.FichaId, versao,
            OperacaoGuiche.Senha(atendimento.Letra, atendimento.Numero), guiche.Estacao.Id, guiche.Estacao.Nome), paineis);
        await OperacaoGuiche.ConfirmarEAvisarAsync(transacao, avisos, aviso, cancellationToken);
        return new AtendimentoAlteradoDto(atendimento.Id, nameof(EstadoAtendimento.Chamado), versao);
    }
}
