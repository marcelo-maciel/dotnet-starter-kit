using FSH.Modules.PAF.Contracts.v1.Atendimentos;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Sessao;
using FSH.Modules.PAF.TempoReal;
using Mediator;

namespace FSH.Modules.PAF.Features.v1.Atendimentos.CancelarAtendimento;

/// <summary>
/// Chamado → Cancelado: the guichê's no-show exit (RF15 item 3, seção 4). It frees the ficha from the
/// exclusivity; the RF12 rehabilitation brings it back. A started atendimento is closed, not cancelled, here.
/// </summary>
public sealed class CancelarAtendimentoCommandHandler(PafDbContext db, SessaoCorrente sessao, ConcessaoCorrente concessao, AtosImpersonacao atos, AvisosTempoReal avisos)
    : ICommandHandler<CancelarAtendimentoCommand, AtendimentoAlteradoDto>
{
    public async ValueTask<AtendimentoAlteradoDto> Handle(CancelarAtendimentoCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var guiche = await OperacaoGuiche.ExigirAsync(db, DonoPosto.Exigir(sessao, concessao), cancellationToken);

        await using var transacao = await db.Database.BeginTransactionAsync(cancellationToken);
        var atendimento = await OperacaoGuiche.TravarAsync(db, atos, guiche, command.AtendimentoId, cancellationToken);
        OperacaoGuiche.ExigirEstado(atendimento, guiche, EstadoAtendimento.Chamado, MensagensGuiche.MensagemNaoChamado);

        var versao = await OperacaoGuiche.TransitarAsync(db, atendimento, $"""
            UPDATE paf."Atendimentos"
               SET "Estado" = 'Cancelado', "CanceladoEm" = now(), "Versao" = "Versao" + 1
             WHERE "TenantId" = {guiche.TenantId} AND "Id" = {atendimento.Id} AND "Estado" = 'Chamado'
            RETURNING "Versao" AS "Value"
            """, cancellationToken);
        await OperacaoGuiche.LiberarPendentesAsync(db, guiche, atendimento, cancellationToken);
        await OperacaoGuiche.SoltarVinculoDeEstacaoInativaAsync(db, guiche.TenantId, guiche.Estacao.Id, cancellationToken);

        var aviso = OperacaoGuiche.AvisoDeEstado(guiche, atendimento, versao, EstadoAtendimento.Cancelado);
        await OperacaoGuiche.ConfirmarEAvisarAsync(transacao, avisos, aviso, cancellationToken);
        return new AtendimentoAlteradoDto(atendimento.Id, nameof(EstadoAtendimento.Cancelado), versao);
    }
}
