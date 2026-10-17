using FSH.Modules.PAF.Contracts.v1.Atendimentos;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Sessao;
using FSH.Modules.PAF.TempoReal;
using Mediator;

namespace FSH.Modules.PAF.Features.v1.Atendimentos.EncerrarAtendimento;

/// <summary>
/// EmAtendimento → Encerrado (RF16). A called but not started atendimento answers "A estação não está atendendo"
/// and writes nothing, so every closed atendimento has its start mark (RF06).
/// </summary>
public sealed class EncerrarAtendimentoCommandHandler(PafDbContext db, SessaoCorrente sessao, AvisosTempoReal avisos)
    : ICommandHandler<EncerrarAtendimentoCommand, AtendimentoAlteradoDto>
{
    public async ValueTask<AtendimentoAlteradoDto> Handle(EncerrarAtendimentoCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var guiche = await OperacaoGuiche.ExigirAsync(db, sessao, cancellationToken);

        await using var transacao = await db.Database.BeginTransactionAsync(cancellationToken);
        var atendimento = await OperacaoGuiche.TravarAsync(db, guiche, command.AtendimentoId, cancellationToken);
        OperacaoGuiche.ExigirEstado(atendimento, guiche, EstadoAtendimento.EmAtendimento, MensagensGuiche.MensagemNaoAtendendo);

        var versao = await OperacaoGuiche.TransitarAsync(db, atendimento, $"""
            UPDATE paf."Atendimentos"
               SET "Estado" = 'Encerrado', "EncerradoEm" = now(), "Versao" = "Versao" + 1
             WHERE "TenantId" = {guiche.TenantId} AND "Id" = {atendimento.Id} AND "Estado" = 'EmAtendimento'
            RETURNING "Versao" AS "Value"
            """, cancellationToken);
        await OperacaoGuiche.LiberarPendentesAsync(db, guiche, atendimento, cancellationToken);

        var aviso = OperacaoGuiche.AvisoDeEstado(guiche, atendimento, versao, EstadoAtendimento.Encerrado);
        await OperacaoGuiche.ConfirmarEAvisarAsync(transacao, avisos, aviso, cancellationToken);
        return new AtendimentoAlteradoDto(atendimento.Id, nameof(EstadoAtendimento.Encerrado), versao);
    }
}
