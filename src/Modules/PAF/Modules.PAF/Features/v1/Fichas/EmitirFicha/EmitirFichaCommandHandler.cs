using FSH.Framework.Core.Exceptions;
using FSH.Modules.PAF.Contracts.v1.Fichas;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Escopo;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Fichas.EmitirFicha;

/// <summary>
/// Emission at the totem (RF11): only a fila of the station's emission role, then the shared core
/// <see cref="EmissaoDeFicha"/> with canal <c>Totem</c>.
/// </summary>
public sealed class EmitirFichaCommandHandler(
    PafDbContext db,
    IUnidadeCorrente unidadeCorrente,
    EmissaoDeFicha emissao) : ICommandHandler<EmitirFichaCommand, FichaEmitidaDto>
{
    public const string RestricaoNumeracao = EmissaoDeFicha.RestricaoNumeracao;
    public const string RestricaoChaveIdempotencia = EmissaoDeFicha.RestricaoChaveIdempotencia;
    public const string MensagemFilaForaDaEstacao = "Fila não disponível nesta estação.";
    public const string MensagemPrioridadeForaDaFila = EmissaoDeFicha.MensagemPrioridadeForaDaFila;
    public const string MensagemIndisponivel = EmissaoDeFicha.MensagemIndisponivel;
    public const string MensagemForaDoHorario = EmissaoDeFicha.MensagemForaDoHorario;
    public const string MensagemSemNumero = EmissaoDeFicha.MensagemSemNumero;
    public const int MaxTentativas = EmissaoDeFicha.MaxTentativas;

    public async ValueTask<FichaEmitidaDto> Handle(EmitirFichaCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var unidadeId = unidadeCorrente.Exigir();

        // Unit-filtered read: a fila of another unidade is not on this station either.
        var naEstacao = await db.EstacaoFilas.AsNoTracking()
            .AnyAsync(ef => ef.EstacaoId == command.EstacaoId && ef.Papel == PapelEstacao.Emissao && ef.FilaId == command.FilaId, cancellationToken)
            .ConfigureAwait(false);
        if (!naEstacao)
        {
            throw new NotFoundException(MensagemFilaForaDaEstacao);
        }

        return await emissao.EmitirAsync(
                new PedidoEmissao(unidadeId, CanalEmissao.Totem, command.EstacaoId, command.FilaId, command.PrioridadeId, command.ChaveIdempotencia),
                cancellationToken)
            .ConfigureAwait(false);
    }
}
