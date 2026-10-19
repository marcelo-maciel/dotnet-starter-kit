using System.Net;
using Finbuckle.MultiTenant.Abstractions;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Shared.Multitenancy;
using FSH.Modules.PAF.Contracts.v1.Fichas;
using FSH.Modules.PAF.Contracts.v1.TempoReal;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Features.v1.Atendimentos;
using FSH.Modules.PAF.Features.v1.Filas;
using FSH.Modules.PAF.Parametros;
using FSH.Modules.PAF.Sessao;
using FSH.Modules.PAF.TempoReal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FSH.Modules.PAF.Features.v1.Fichas.EmitirFicha;

/// <summary>
/// One ficha emission of the current unidade, whatever the channel (RF11, seção 4.1, ADR 0005).
/// <see cref="CanalEmissao"/> and the emitting station are data stored on the ficha; which filas a channel may emit is
/// the caller's check (the totem's emission role, the guichê's whole unidade), done before this.
/// </summary>
public sealed record PedidoEmissao(Guid UnidadeId, CanalEmissao Canal, Guid EstacaoId, Guid FilaId, Guid PrioridadeId, string ChaveIdempotencia);

/// <summary>
/// The emission core shared by the totem and the guichê: the number is the day's max + 1 per (unidade, fila,
/// prioridade); a collision on UX_Fichas_Numeracao retries in a NEW transaction, because PostgreSQL aborts the
/// current one. A collision on UX_Fichas_ChaveIdempotencia is a resend: it returns the stored ficha.
/// </summary>
public sealed partial class EmissaoDeFicha(
    PafDbContext db,
    IMultiTenantContextAccessor<AppTenantInfo> tenantAccessor,
    AvisosTempoReal avisos,
    LeitorParametros parametros,
    AtosImpersonacao atos,
    ILogger<EmissaoDeFicha> logger)
{
    public const string RestricaoNumeracao = "UX_Fichas_Numeracao";
    public const string RestricaoChaveIdempotencia = "UX_Fichas_ChaveIdempotencia";
    public const string MensagemPrioridadeForaDaFila = "Prioridade não disponível nesta fila.";
    public const string MensagemIndisponivel = "Fila indisponível para emissão.";
    public const string MensagemForaDoHorario = "Fila fora do horário de emissão.";
    public const string MensagemSemNumero = "Não foi possível numerar a ficha. Tente de novo.";

    // Every concurrent emitter of the same combination loses at most once per winner, so N simultaneous
    // emitters need at most N - 1 retries; 10 covers far more totens and guichês per fila than a store has (ADR 0005).
    public const int MaxTentativas = 10;

    public async Task<FichaEmitidaDto> EmitirAsync(PedidoEmissao pedido, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pedido);
        var tenantId = tenantAccessor.MultiTenantContext?.TenantInfo?.Id ?? throw new UnauthorizedException();

        var emissao = await ValidarAsync(pedido, ct).ConfigureAwait(false);

        for (var tentativa = 1; tentativa <= MaxTentativas; tentativa++)
        {
            var resultado = await TentarAsync(pedido, emissao, tenantId, ct).ConfigureAwait(false);
            switch (resultado)
            {
                case Tentativa.Emitida emitida:
                    if (tentativa > 1)
                    {
                        LogNumeracaoRepetida(logger, tenantId, pedido.UnidadeId, pedido.FilaId, pedido.PrioridadeId, tentativa);
                    }

                    await avisos.EnviarAsync(emitida.Aviso, ct).ConfigureAwait(false);
                    return emitida.Ficha;

                case Tentativa.JaEmitida:
                    return await FichaJaEmitidaAsync(pedido, emissao, ct).ConfigureAwait(false);
            }
        }

        LogNumeracaoEsgotada(logger, tenantId, pedido.UnidadeId, pedido.FilaId, pedido.PrioridadeId, MaxTentativas);
        throw new CustomException(MensagemSemNumero, [], HttpStatusCode.ServiceUnavailable);
    }

    private async Task<Emissao> ValidarAsync(PedidoEmissao pedido, CancellationToken ct)
    {
        // Unit-filtered reads: a fila or prioridade of another unidade does not exist here.
        var associacao = await db.FilaPrioridades.AsNoTracking()
            .FirstOrDefaultAsync(fp => fp.FilaId == pedido.FilaId && fp.PrioridadeId == pedido.PrioridadeId, ct)
            .ConfigureAwait(false)
            ?? throw new NotFoundException(MensagemPrioridadeForaDaFila);

        var fila = await db.Filas.AsNoTracking().SingleAsync(f => f.Id == pedido.FilaId, ct).ConfigureAwait(false);
        var prioridade = await db.Prioridades.AsNoTracking().SingleAsync(p => p.Id == pedido.PrioridadeId, ct).ConfigureAwait(false);
        var unidade = await db.Unidades.AsNoTracking().SingleAsync(u => u.Id == pedido.UnidadeId, ct).ConfigureAwait(false);

        // Deactivation blocks new emissions only; fichas already issued stay callable (seção 4).
        if (!unidade.Ativa || !fila.Ativa || !prioridade.Ativa)
        {
            throw new CustomException(MensagemIndisponivel, [], HttpStatusCode.Conflict);
        }

        // RF06 "Parâmetros das fichas" of the unidade, read once per emission and printed as they are now (a replay too).
        var impressao = await parametros.CarregarAsync(pedido.UnidadeId, ct).ConfigureAwait(false);
        return new Emissao(unidade, fila, prioridade, associacao, new ImpressaoFicha(
            impressao.Ler(CatalogoParametros.TituloFichas),
            impressao.Ler(CatalogoParametros.MensagemRodapeFicha),
            impressao.Ler(CatalogoParametros.ImprimirQrFicha)));
    }

    private async Task<Tentativa> TentarAsync(PedidoEmissao pedido, Emissao emissao, string tenantId, CancellationToken ct)
    {
        // A failed attempt leaves its Added entities tracked; the next attempt starts clean.
        db.ChangeTracker.Clear();
        await using var transacao = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        var agora = await PafRelogio.AgoraEmSaoPauloAsync(db, ct).ConfigureAwait(false);
        var hora = TimeOnly.FromDateTime(agora);
        // The window blocks only emission, never the call (RF02); no window crosses midnight.
        if (!Fila.JanelaPermiteEmissao(emissao.Fila.HoraInicial, emissao.Fila.HoraFinal, hora))
        {
            throw new CustomException(MensagemForaDoHorario, [], HttpStatusCode.Conflict);
        }

        // Checked inside the transaction, under the shared coverage lock held to commit: a station write that
        // removes the last guichê waits for this emission, and every emission after it sees the new cadastro.
        await CoberturaGuiche.TravarParaEmissaoAsync(db, tenantId, emissao.Unidade.Id, ct).ConfigureAwait(false);
        if (!await CoberturaGuiche.CobreAsync(db, pedido.FilaId, pedido.PrioridadeId, ct).ConfigureAwait(false))
        {
            throw new CustomException(CoberturaGuiche.MensagemSemCobertura, [], HttpStatusCode.Conflict);
        }

        var hoje = DateOnly.FromDateTime(agora);
        var maior = await db.Fichas.AsNoTracking()
            .Where(f => f.UnidadeId == emissao.Unidade.Id
                && f.FilaId == pedido.FilaId
                && f.PrioridadeId == pedido.PrioridadeId
                && f.DataOperacional == hoje)
            .MaxAsync(f => (int?)f.Numero, ct)
            .ConfigureAwait(false);
        var numero = maior + 1 ?? emissao.Associacao.NumeroInicial;

        var ficha = Ficha.Criar(emissao.Unidade.Id, pedido.FilaId, pedido.PrioridadeId, emissao.Associacao.Letra, numero,
            pedido.Canal, pedido.EstacaoId, pedido.ChaveIdempotencia);
        var atendimento = Atendimento.Criar(emissao.Unidade.Id, ficha.Id, pedido.FilaId, pedido.PrioridadeId, atendimentoOrigemId: null);
        db.Fichas.Add(ficha);
        db.Atendimentos.Add(atendimento);
        // The Clear above dropped the act the pipeline noted; noting it again puts it in this attempt's SaveChanges,
        // so it commits or rolls back with the ficha. Without impersonation (every totem) this is a no-op.
        atos.Anotar(atendimento.Id);
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

        var aviso = FilaAtualizadaPaf.Criar(tenantId, OperacaoGuiche.Correlacao(), new FilaAtualizada(Guid.CreateVersion7(), emissao.Unidade.Id, pedido.FilaId));
        await avisos.GravarAsync(aviso, ct).ConfigureAwait(false);
        await transacao.CommitAsync(ct).ConfigureAwait(false);

        return new Tentativa.Emitida(Dto(ficha, atendimento.Id, emissao), aviso);
    }

    private async Task<FichaEmitidaDto> FichaJaEmitidaAsync(PedidoEmissao pedido, Emissao emissao, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        // A resend writes nothing, so there is no act to record (AtosImpersonacao.Descartar).
        atos.Descartar();
        var ficha = await db.Fichas.AsNoTracking()
            .SingleAsync(f => f.EstacaoId == pedido.EstacaoId && f.ChaveIdempotencia == pedido.ChaveIdempotencia, ct)
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
            emissao.Unidade.Nome, emissao.Fila.NomeExibicao, emissao.Prioridade.Nome, PafRelogio.EmSaoPaulo(ficha.CriadoEm),
            emissao.Impressao.Titulo, emissao.Impressao.Rodape, emissao.Impressao.ImprimirQr);

    private sealed record Emissao(Unidade Unidade, Fila Fila, Prioridade Prioridade, FilaPrioridade Associacao, ImpressaoFicha Impressao);

    private sealed record ImpressaoFicha(string Titulo, string Rodape, bool ImprimirQr);

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
