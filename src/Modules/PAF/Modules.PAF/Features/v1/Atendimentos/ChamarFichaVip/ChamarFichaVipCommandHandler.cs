using System.Globalization;
using System.Net;
using FSH.Framework.Core.Exceptions;
using FSH.Modules.PAF.Contracts.v1.Atendimentos;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Features.v1.Atendimentos.ChamarFichaEscolhida;
using FSH.Modules.PAF.Features.v1.Atendimentos.ChamarProximaFicha;
using FSH.Modules.PAF.Features.v1.Atendimentos.ReabilitarAtendimentoDaFicha;
using FSH.Modules.PAF.Parametros;
using FSH.Modules.PAF.Sessao;
using FSH.Modules.PAF.TempoReal;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Atendimentos.ChamarFichaVip;

/// <summary>
/// RF19 "atendimento VIP": the guichê calls the ficha named by its printed senha (D02), date and fila, out of the queue
/// order and nothing else (requisitos RF19, 8.1 l.1030). The ficha is found by its operational date; whether its
/// atendimento is still callable follows the atendimento's eligibility date, through the same predicate as every other
/// call (ChamadaDeFicha). A ficha in progress on another guichê is refused naming that guichê. Same lock order as
/// chamar-próximo and the chosen call (guichê link, then ficha rows), waiting for the ficha lock like the chosen call.
/// </summary>
public sealed class ChamarFichaVipCommandHandler(
    PafDbContext db,
    SessaoCorrente sessao,
    AvisosTempoReal avisos,
    LeitorParametros parametros) : ICommandHandler<ChamarFichaVipCommand, ChamadaDto>
{
    public const string MensagemNaoEncontrada = "Nenhuma ficha aguardando com esta senha, data e fila nesta unidade.";

    public static string MensagemEmCurso(string nomeEstacao) =>
        $"A ficha está em atendimento no guichê {nomeEstacao}.";

    public async ValueTask<ChamadaDto> Handle(ChamarFichaVipCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var guiche = await OperacaoGuiche.ExigirAsync(db, sessao, cancellationToken);
        if (!guiche.Estacao.Ativa)
        {
            throw OperacaoGuiche.Conflito(ChamarProximaFichaCommandHandler.MensagemEstacaoInativa);
        }

        // Only gives the message before trying; the guarantee is UX_Atendimentos_EstacaoEmCurso (ARQ l.144).
        var guicheEmCurso = await db.Atendimentos.AsNoTracking()
            .AnyAsync(a => a.EstacaoId == guiche.Estacao.Id
                && (a.Estado == EstadoAtendimento.Chamado || a.Estado == EstadoAtendimento.EmAtendimento), cancellationToken);
        if (guicheEmCurso)
        {
            throw OperacaoGuiche.Conflito(ChamarProximaFichaCommandHandler.MensagemGuicheEmCurso);
        }

        var atendimentoId = await LocalizarAsync(Normalizar(command.Senha), command.Data, command.FilaId, cancellationToken);
        var somenteDataAtual = await parametros.LerAsync(CatalogoParametros.SomenteFichasDaDataAtual, guiche.Estacao.UnidadeId, cancellationToken);
        try
        {
            return await ChamarAsync(guiche, atendimentoId, somenteDataAtual, cancellationToken);
        }
        catch (Exception ex) when (PafRestricoes.Violou(ex, ChamarProximaFichaCommandHandler.RestricaoEstacaoEmCurso))
        {
            throw OperacaoGuiche.Conflito(ChamarProximaFichaCommandHandler.MensagemGuicheEmCurso);
        }
        catch (Exception ex) when (PafRestricoes.Violou(ex, ChamarProximaFichaCommandHandler.RestricaoFichaEmCurso))
        {
            throw OperacaoGuiche.Conflito(ChamarFichaEscolhidaCommandHandler.MensagemJaChamada);
        }
        catch (Exception ex) when (PafRestricoes.Impasse(ex))
        {
            throw new CustomException(ChamarProximaFichaCommandHandler.MensagemDisputa, [], HttpStatusCode.ServiceUnavailable);
        }
    }

    // Same normalization as the letter at the cadastro (LetraDaSenha), so a lower-case or padded entry finds the senha.
    private static string Normalizar(string senha) => senha.Trim().ToUpperInvariant();

    /// <summary>
    /// Every way to read the senha as a letter (1 to LetraMaxLength characters) followed by the printed number (digits,
    /// no zero padding, seção 4.1). A letter may itself hold digits, so the split is never guessed: all readings are
    /// tried and more than one matching ficha is ambiguous.
    /// </summary>
    public static IReadOnlyList<(string Letra, int Numero)> Leituras(string senha)
    {
        ArgumentNullException.ThrowIfNull(senha);
        var leituras = new List<(string, int)>();
        for (var tamanho = 1; tamanho <= Math.Min(FilaPrioridade.LetraMaxLength, senha.Length - 1); tamanho++)
        {
            var digitos = senha[tamanho..];
            if (digitos[0] != '0' && digitos.All(char.IsAsciiDigit)
                && int.TryParse(digitos, NumberStyles.None, CultureInfo.InvariantCulture, out var numero))
            {
                leituras.Add((senha[..tamanho], numero));
            }
        }

        return leituras;
    }

    /// <summary>
    /// Unit-filtered reads, so another unit's ficha is simply not found. A senha that matches no ficha, or more than
    /// one, is 404. Exactly one waiting atendimento of the ficha in the fila, else 404.
    /// </summary>
    private async Task<Guid> LocalizarAsync(string senha, DateOnly data, Guid filaId, CancellationToken ct)
    {
        var leituras = Leituras(senha);
        var letras = leituras.Select(l => l.Letra).ToList();
        var numeros = leituras.Select(l => l.Numero).ToList();
        // The two lists cross in SQL (a few rows of one unit and day); the exact pair is kept here.
        var candidatas = await db.Fichas.AsNoTracking()
            .Where(f => f.DataOperacional == data && letras.Contains(f.Letra) && numeros.Contains(f.Numero))
            .Select(f => new { f.Id, f.Letra, f.Numero })
            .ToListAsync(ct);
        var fichas = candidatas.Where(f => leituras.Contains((f.Letra, f.Numero))).Select(f => f.Id).ToList();
        if (fichas.Count != 1)
        {
            throw new NotFoundException(MensagemNaoEncontrada);
        }

        var aguardando = await db.Atendimentos.AsNoTracking()
            .Where(a => a.FichaId == fichas[0] && a.FilaId == filaId && a.Estado == EstadoAtendimento.Aguardando)
            .Select(a => a.Id)
            .Take(2)
            .ToListAsync(ct);
        if (aguardando.Count == 1)
        {
            return aguardando[0];
        }

        // Nothing waiting in that fila: when the ficha is being served somewhere, say where instead of "not found" (RF19).
        await RecusarSeEmCursoAsync(fichas[0], ct);
        throw new NotFoundException(MensagemNaoEncontrada);
    }

    private async Task<ChamadaDto> ChamarAsync(Guiche guiche, Guid atendimentoId, bool somenteDataAtual, CancellationToken ct)
    {
        await using var transacao = await db.Database.BeginTransactionAsync(ct);

        var estacaoDoVinculo = await VinculosGuiche.TravarAtivoDaSessaoAsync(db, guiche.TenantId, guiche.SessaoId, ct);
        if (estacaoDoVinculo != guiche.Estacao.Id)
        {
            throw OperacaoGuiche.Conflito(MensagensGuiche.MensagemSemGuiche);
        }

        var atendimento = await OperacaoGuiche.TravarFichaAsync(db, guiche.TenantId, atendimentoId, ct);

        // Under the lock: a call that committed while this request waited is seen here and named.
        await RecusarSeEmCursoAsync(atendimento.FichaId, ct);
        if (atendimento.Estado != EstadoAtendimento.Aguardando)
        {
            throw new NotFoundException(MensagemNaoEncontrada);
        }

        var elegiveis = await db.Database.SqlQuery<Candidato>(ChamarFichaEscolhidaCommandHandler.ConsultaEscolhida(
            guiche.TenantId, guiche.Estacao.UnidadeId, guiche.Estacao.Id, somenteDataAtual, atendimentoId)).ToListAsync(ct);
        if (elegiveis.Count == 0)
        {
            // RF19: an atendimento the date parameter made uncallable gets the same refusal as the RF12 reabilitar.
            var foraDoDia = somenteDataAtual && atendimento.DataElegibilidade != await PafRelogio.HojeAsync(db, ct);
            throw OperacaoGuiche.Conflito(foraDoDia
                ? ReabilitarAtendimentoDaFichaCommandHandler.MensagemForaDoDia
                : ChamarFichaEscolhidaCommandHandler.MensagemNaoElegivel);
        }

        var candidato = elegiveis[0];
        var versao = await ChamadaDeFicha.MarcarChamadoAsync(db, guiche, candidato, ct)
            ?? throw OperacaoGuiche.Conflito(ChamarFichaEscolhidaCommandHandler.MensagemJaChamada);
        return await ChamadaDeFicha.ConfirmarAsync(db, avisos, transacao, guiche, candidato, versao, ct);
    }

    private async Task RecusarSeEmCursoAsync(Guid fichaId, CancellationToken ct)
    {
        var emCurso = await (
                from a in db.Atendimentos.AsNoTracking()
                join e in db.Estacoes.AsNoTracking() on a.EstacaoId equals e.Id
                where a.FichaId == fichaId
                      && (a.Estado == EstadoAtendimento.Chamado || a.Estado == EstadoAtendimento.EmAtendimento)
                select e.Nome)
            .FirstOrDefaultAsync(ct);
        if (emCurso is not null)
        {
            throw OperacaoGuiche.Conflito(MensagemEmCurso(emCurso));
        }
    }
}
