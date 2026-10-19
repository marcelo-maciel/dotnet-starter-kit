using FSH.Framework.Core.Exceptions;
using FSH.Modules.PAF.Contracts.v1.Fichas;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Escopo;
using FSH.Modules.PAF.Features.v1.Atendimentos;
using FSH.Modules.PAF.Features.v1.Fichas.EmitirFicha;
using FSH.Modules.PAF.Sessao;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Fichas.EmitirFichaNoGuiche;

/// <summary>
/// Manual emission at the guichê (RF11, RF04 l.185): the emitting station is the guichê of the session's link, never a
/// parameter of the screen, and any fila of the current unidade may be emitted (coverage, window and deactivation are
/// the shared core's checks), numbered in the same sequence as the totem.
/// </summary>
public sealed class EmitirFichaNoGuicheCommandHandler(
    PafDbContext db,
    SessaoCorrente sessao,
    IUnidadeCorrente unidadeCorrente,
    EmissaoDeFicha emissao) : ICommandHandler<EmitirFichaNoGuicheCommand, FichaEmitidaDto>
{
    public const string MensagemFilaNaoEncontrada = "Fila não encontrada.";
    public const string MensagemEstacaoInativa = "Estação inativa não emite fichas.";

    public async ValueTask<FichaEmitidaDto> Handle(EmitirFichaNoGuicheCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var guiche = await OperacaoGuiche.ExigirAsync(db, sessao, cancellationToken).ConfigureAwait(false);
        // A deactivated station keeps only its atendimento in progress (RF04); a new ficha is a new operation.
        if (!guiche.Estacao.Ativa)
        {
            throw OperacaoGuiche.Conflito(MensagemEstacaoInativa);
        }

        var unidadeId = unidadeCorrente.Exigir();
        // Unit-filtered: a fila of another unidade does not exist here.
        if (!await db.Filas.AsNoTracking().AnyAsync(f => f.Id == command.FilaId, cancellationToken).ConfigureAwait(false))
        {
            throw new NotFoundException(MensagemFilaNaoEncontrada);
        }

        return await emissao.EmitirAsync(
                new PedidoEmissao(unidadeId, CanalEmissao.Guiche, guiche.Estacao.Id, command.FilaId, command.PrioridadeId, command.ChaveIdempotencia),
                cancellationToken)
            .ConfigureAwait(false);
    }
}
