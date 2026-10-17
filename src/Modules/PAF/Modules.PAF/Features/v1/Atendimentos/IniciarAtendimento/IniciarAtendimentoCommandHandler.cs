using FSH.Modules.PAF.Contracts.v1.Atendimentos;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Sessao;
using FSH.Modules.PAF.TempoReal;
using Mediator;

namespace FSH.Modules.PAF.Features.v1.Atendimentos.IniciarAtendimento;

/// <summary>Chamado → EmAtendimento on the session's guichê, under the ficha lock (RF06, RF15 item 1, seção 8).</summary>
public sealed class IniciarAtendimentoCommandHandler(PafDbContext db, SessaoCorrente sessao, AvisosTempoReal avisos)
    : ICommandHandler<IniciarAtendimentoCommand, AtendimentoAlteradoDto>
{
    public async ValueTask<AtendimentoAlteradoDto> Handle(IniciarAtendimentoCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var guiche = await OperacaoGuiche.ExigirAsync(db, sessao, cancellationToken);

        await using var transacao = await db.Database.BeginTransactionAsync(cancellationToken);
        var atendimento = await OperacaoGuiche.TravarAsync(db, guiche, command.AtendimentoId, cancellationToken);
        OperacaoGuiche.ExigirEstado(atendimento, guiche, EstadoAtendimento.Chamado, MensagensGuiche.MensagemNaoChamado);

        var versao = await OperacaoGuiche.TransitarAsync(db, atendimento, $"""
            UPDATE paf."Atendimentos"
               SET "Estado" = 'EmAtendimento', "IniciadoEm" = now(), "Versao" = "Versao" + 1
             WHERE "TenantId" = {guiche.TenantId} AND "Id" = {atendimento.Id} AND "Estado" = 'Chamado'
            RETURNING "Versao" AS "Value"
            """, cancellationToken);

        var aviso = OperacaoGuiche.AvisoDeEstado(guiche, atendimento, versao, EstadoAtendimento.EmAtendimento);
        await OperacaoGuiche.ConfirmarEAvisarAsync(transacao, avisos, aviso, cancellationToken);
        return new AtendimentoAlteradoDto(atendimento.Id, nameof(EstadoAtendimento.EmAtendimento), versao);
    }
}
