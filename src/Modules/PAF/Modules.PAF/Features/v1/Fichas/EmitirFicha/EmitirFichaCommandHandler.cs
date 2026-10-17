using System.Net;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.PAF.Contracts.v1.Fichas;
using FSH.Modules.PAF.Contracts.v1.TempoReal;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Escopo;
using FSH.Modules.PAF.Features.v1.Atendimentos;
using FSH.Modules.PAF.TempoReal;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FSH.Modules.PAF.Features.v1.Fichas.EmitirFicha;

/// <summary>
/// Emission at the totem (RF11, seção 4.1, ADR 0005). The number is the day's max + 1 per (unidade, fila,
/// prioridade); a collision on UX_Fichas_Numeracao retries in a NEW transaction, because PostgreSQL aborts the
/// current one. A collision on UX_Fichas_ChaveIdempotencia is a resend: it returns the stored ficha.
/// </summary>
public sealed partial class EmitirFichaCommandHandler(
    PafDbContext db,
    IUnidadeCorrente unidadeCorrente,
    IMultiTenantContextAccessor<AppTenantInfo> tenantAccessor,
    AvisosTempoReal avisos,
    ILogger<EmitirFichaCommandHandler> logger) : ICommandHandler<EmitirFichaCommand, FichaEmitidaDto>
{
    public const string RestricaoNumeracao = "UX_Fichas_Numeracao";
    public const string RestricaoChaveIdempotencia = "UX_Fichas_ChaveIdempotencia";
    public const string MensagemFilaForaDaEstacao = "Fila não disponível nesta estação.";
    public const string MensagemPrioridadeForaDaFila = "Prioridade não disponível nesta fila.";
    public const string MensagemIndisponivel = "Fila indisponível para emissão.";
    public const string MensagemForaDoHorario = "Fila fora do horário de emissão.";
    public const string MensagemSemNumero = "Não foi possível numerar a ficha. Tente de novo.";

    // Every concurrent emitter of the same combination loses at most once per winner, so N simultaneous
    // totens need at most N - 1 retries; 10 covers far more totens per fila than a store has (ADR 0005).
    public const int MaxTentativas = 10;

    public async ValueTask<FichaEmitidaDto> Handle(EmitirFichaCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var unidadeId = unidadeCorrente.Exigir();
        var tenantId = tenantAccessor.MultiTenantContext?.TenantInfo?.Id ?? throw new UnauthorizedException();

        var emissao = await ValidarAsync(command, unidadeId, cancellationToken).ConfigureAwait(false);

        for (var tentativa = 1; tentativa <= MaxTentativas; tentativa++)
        {
            var resultado = await TentarAsync(command, emissao, tenantId, cancellationToken).ConfigureAwait(false);
            switch (resultado)
            {
                case Tentativa.Emitida emitida:
                    if (tentativa > 1)
                    {
                        LogNumeracaoRepetida(logger, tenantId, unidadeId, command.FilaId, command.PrioridadeId, tentativa);
                    }

                    await avisos.EnviarAsync(emitida.Aviso, cancellationToken).ConfigureAwait(false);
                    return emitida.Ficha;

                case Tentativa.JaEmitida:
                    return await FichaJaEmitidaAsync(command, emissao, cancellationToken).ConfigureAwait(false);
            }
        }

        LogNumeracaoEsgotada(logger, tenantId, unidadeId, command.FilaId, command.PrioridadeId, MaxTentativas);
        throw new CustomException(MensagemSemNumero, [], HttpStatusCode.ServiceUnavailable);
    }

    private async Task<Emissao> ValidarAsync(EmitirFichaCommand command, Guid unidadeId, CancellationToken ct)
    {
        // Unit-filtered reads: a fila or prioridade of another unidade does not exist here.
        var naEstacao = await db.EstacaoFilas.AsNoTracking()
            .AnyAsync(ef => ef.EstacaoId == command.EstacaoId && ef.Papel == PapelEstacao.Emissao && ef.FilaId == command.FilaId, ct)
            .ConfigureAwait(false);
        if (!naEstacao)
        {
            throw new NotFoundException(MensagemFilaForaDaEstacao);
        }

        var associacao = await db.FilaPrioridades.AsNoTracking()
            .FirstOrDefaultAsync(fp => fp.FilaId == command.FilaId && fp.PrioridadeId == command.PrioridadeId, ct)
            .ConfigureAwait(false)
            ?? throw new NotFoundException(MensagemPrioridadeForaDaFila);

        var fila = await db.Filas.AsNoTracking().SingleAsync(f => f.Id == command.FilaId, ct).ConfigureAwait(false);
        var prioridade = await db.Prioridades.AsNoTracking().SingleAsync(p => p.Id == command.PrioridadeId, ct).ConfigureAwait(false);
        var unidade = await db.Unidades.AsNoTracking().SingleAsync(u => u.Id == unidadeId, ct).ConfigureAwait(false);

        // Deactivation blocks new emissions only; fichas already issued stay callable (seção 4).
        if (!unidade.Ativa || !fila.Ativa || !prioridade.Ativa)
        {
            throw new CustomException(MensagemIndisponivel, [], HttpStatusCode.Conflict);
        }

        return new Emissao(unidade, fila, prioridade, associacao);
    }

    private async Task<Tentativa> TentarAsync(EmitirFichaCommand command, Emissao emissao, string tenantId, CancellationToken ct)
    {
        // A failed attempt leaves its Added entities tracked; the next attempt starts clean.
        db.ChangeTracker.Clear();
        await using var transacao = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        var agora = await PafRelogio.AgoraEmSaoPauloAsync(db, ct).ConfigureAwait(false);
        var hora = TimeOnly.FromDateTime(agora);
        // The window blocks only emission, never the call (RF02); no window crosses midnight.
        if (emissao.Fila.HoraInicial is { } inicio && emissao.Fila.HoraFinal is { } fim && (hora < inicio || hora > fim))
        {
            throw new CustomException(MensagemForaDoHorario, [], HttpStatusCode.Conflict);
        }

        var hoje = DateOnly.FromDateTime(agora);
        var maior = await db.Fichas.AsNoTracking()
            .Where(f => f.UnidadeId == emissao.Unidade.Id
                && f.FilaId == command.FilaId
                && f.PrioridadeId == command.PrioridadeId
                && f.DataOperacional == hoje)
            .MaxAsync(f => (int?)f.Numero, ct)
            .ConfigureAwait(false);
        var numero = maior + 1 ?? emissao.Associacao.NumeroInicial;

        var ficha = Ficha.Criar(emissao.Unidade.Id, command.FilaId, command.PrioridadeId, emissao.Associacao.Letra, numero,
            CanalEmissao.Totem, command.EstacaoId, command.ChaveIdempotencia);
        var atendimento = Atendimento.Criar(emissao.Unidade.Id, ficha.Id, command.FilaId, command.PrioridadeId, atendimentoOrigemId: null);
        db.Fichas.Add(ficha);
        db.Atendimentos.Add(atendimento);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (PafRestricoes.Violou(ex, RestricaoNumeracao))
        {
            return new Tentativa.Colidiu();
        }
        catch (DbUpdateException ex) when (PafRestricoes.Violou(ex, RestricaoChaveIdempotencia))
        {
            return new Tentativa.JaEmitida();
        }

        var aviso = FilaAtualizadaPaf.Criar(tenantId, OperacaoGuiche.Correlacao(), new FilaAtualizada(Guid.CreateVersion7(), emissao.Unidade.Id, command.FilaId));
        await avisos.GravarAsync(aviso, ct).ConfigureAwait(false);
        await transacao.CommitAsync(ct).ConfigureAwait(false);

        return new Tentativa.Emitida(Dto(ficha, atendimento.Id, emissao), aviso);
    }

    private async Task<FichaEmitidaDto> FichaJaEmitidaAsync(EmitirFichaCommand command, Emissao emissao, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var ficha = await db.Fichas.AsNoTracking()
            .SingleAsync(f => f.EstacaoId == command.EstacaoId && f.ChaveIdempotencia == command.ChaveIdempotencia, ct)
            .ConfigureAwait(false);
        var atendimentoId = await db.Atendimentos.AsNoTracking()
            .Where(a => a.FichaId == ficha.Id && a.AtendimentoOrigemId == null)
            .Select(a => a.Id)
            .SingleAsync(ct)
            .ConfigureAwait(false);

        // The kit replays by key without comparing bodies; the stored ficha answers, even for another fila.
        if (ficha.FilaId == emissao.Fila.Id && ficha.PrioridadeId == emissao.Prioridade.Id)
        {
            return Dto(ficha, atendimentoId, emissao);
        }

        var fila = await db.Filas.AsNoTracking().SingleAsync(f => f.Id == ficha.FilaId, ct).ConfigureAwait(false);
        var prioridade = await db.Prioridades.AsNoTracking().SingleAsync(p => p.Id == ficha.PrioridadeId, ct).ConfigureAwait(false);
        return Dto(ficha, atendimentoId, emissao with { Fila = fila, Prioridade = prioridade });
    }

    private static FichaEmitidaDto Dto(Ficha ficha, Guid atendimentoId, Emissao emissao) =>
        new(ficha.Id, atendimentoId, ficha.IdentificadorPublico, OperacaoGuiche.Senha(ficha.Letra, ficha.Numero), ficha.Letra, ficha.Numero,
            emissao.Unidade.Nome, emissao.Fila.NomeExibicao, emissao.Prioridade.Nome, PafRelogio.EmSaoPaulo(ficha.CriadoEm));

    private sealed record Emissao(Unidade Unidade, Fila Fila, Prioridade Prioridade, FilaPrioridade Associacao);

    private abstract record Tentativa
    {
        public sealed record Emitida(FichaEmitidaDto Ficha, AvisoPaf Aviso) : Tentativa;

        public sealed record Colidiu : Tentativa;

        public sealed record JaEmitida : Tentativa;
    }

    [LoggerMessage(EventId = 9030, Level = LogLevel.Information,
        Message = "Numeracao da ficha colidiu e foi repetida em transacao nova; tenant={TenantId} unidade={UnidadeId} fila={FilaId} prioridade={PrioridadeId} tentativas={Tentativas}")]
    private static partial void LogNumeracaoRepetida(ILogger logger, string tenantId, Guid unidadeId, Guid filaId, Guid prioridadeId, int tentativas);

    [LoggerMessage(EventId = 9031, Level = LogLevel.Error,
        Message = "Numeracao da ficha esgotou as tentativas; tenant={TenantId} unidade={UnidadeId} fila={FilaId} prioridade={PrioridadeId} tentativas={Tentativas}")]
    private static partial void LogNumeracaoEsgotada(ILogger logger, string tenantId, Guid unidadeId, Guid filaId, Guid prioridadeId, int tentativas);
}
