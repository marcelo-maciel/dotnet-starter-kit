using System.Net;
using FSH.Framework.Core.Exceptions;
using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.PAF.Contracts.v1.Sessao;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Contracts.v1.TempoReal;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Features.v1.Atendimentos;
using FSH.Modules.PAF.Sessao;
using FSH.Modules.PAF.TempoReal;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FSH.Modules.PAF.Features.v1.Sessao.SetGuiche;

/// <summary>
/// Seleção do guichê depois do login (ADR 0006, seção 5): um guichê por sessão e uma sessão por guichê. Trocar de
/// guichê solta o atual na mesma transação e é recusado com atendimento em curso nele. Vínculo de sessão morta é
/// solto e a escolha aceita no mesmo ato; o de sessão viva recusa informando quem está no guichê. Atendimento
/// chamado ou em atendimento de outro usuário no guichê é pendência: sem liberá-la a seleção é recusada e o guichê
/// fica livre para o dono voltar; liberando, ela é cancelada nesta transação (seção 5, RF04, RF12).
/// </summary>
public sealed class SetGuicheCommandHandler(
    PafDbContext db,
    SessaoCorrente sessao,
    ConcessaoCorrente concessao,
    AtosImpersonacao atos,
    ISessionService sessoes,
    Licencas licencas,
    AvisosTempoReal avisos,
    ConexoesPaf conexoes,
    IImpersonationGrantService concessoes,
    TimeProvider relogio) : ICommandHandler<SetGuicheCommand>
{
    public const string RestricaoEstacaoAtiva = "UX_SessoesEstacao_EstacaoAtiva";
    public const string MensagemNaoEncontrado = "Guichê não encontrado na unidade corrente.";
    public const string MensagemNaoEGuiche = "Esta estação não é um guichê ativo.";
    public const string MensagemEmUsoPeloSuporte = "O guichê está em uso pelo suporte.";
    public const string MensagemAtendimentoEmCurso = VinculosGuiche.MensagemAtendimentoEmCurso;
    public const string MensagemEscolhidoAgora = "O guichê acabou de ser escolhido por outra pessoa.";

    // The kit's grant check sits on the selection path: a stuck grant service must not hold the guichê lock long.
    private static readonly TimeSpan PrazoConcessao = TimeSpan.FromSeconds(2);

    internal static readonly TipoEstacao[] TiposComGuiche = [TipoEstacao.Guiche, TipoEstacao.EmissaoGuiche, TipoEstacao.GuichePainel];

    public static string MensagemEmUso(string nomeEstacao, string ocupante) =>
        $"O guichê {nomeEstacao} está em uso por {ocupante}.";

    public static string MensagemPendencia(string nomeEstacao, string senha) =>
        $"O guichê {nomeEstacao} tem a senha {senha} pendente de outro atendente. Libere a pendência para usar este guichê.";

    public async ValueTask<Unit> Handle(SetGuicheCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        // S11: the post's owner is the session or, under the Codefi's impersonation, the grant, whose link is its own:
        // the real user's link is never released by the support, and a guichê he holds refuses the support with 409.
        var dono = DonoPosto.Exigir(sessao, concessao);
        var tenantId = dono.TenantId;

        // The unit filter limits the choice to the current unit's stations (ADR 0006).
        var estacao = await db.Estacoes.AsNoTracking().FirstOrDefaultAsync(e => e.Id == command.EstacaoId, cancellationToken)
            ?? throw new NotFoundException(MensagemNaoEncontrado);
        if (!estacao.Ativa || !TiposComGuiche.Contains(estacao.Tipo))
        {
            throw new CustomException(MensagemNaoEGuiche, [], HttpStatusCode.Conflict);
        }

        await using var transacao = await db.Database.BeginTransactionAsync(cancellationToken);
        var atual = await VinculosGuiche.AtivoDoDonoAsync(db, dono, cancellationToken);
        if (atual?.EstacaoId == estacao.Id)
        {
            // Same guichê again writes nothing: the act the pipeline noted under impersonation describes no write.
            atos.Descartar();
            return Unit.Value;
        }

        if (atual is not null)
        {
            await VinculosGuiche.SoltarSemAtendimentoEmCursoAsync(db, tenantId, atual, cancellationToken);
        }

        SessaoEstacao? ocupanteSolto = null;
        if (await VinculosGuiche.AtivoDaEstacaoAsync(db, estacao.Id, cancellationToken) is { } ocupante)
        {
            ocupanteSolto = await SoltarOcupanteMortoAsync(tenantId, estacao, ocupante, cancellationToken);
        }

        IReadOnlyList<AvisoPaf>? avisosLiberacao = null;
        Guid? pendenciaCancelada = null;
        if (await PendenciaDeOutroUsuarioAsync(db, estacao.Id, dono.UsuarioId, cancellationToken) is { } pendencia)
        {
            if (!command.LiberarPendencia)
            {
                throw new CustomException(MensagemPendencia(estacao.Nome, pendencia.Senha), [], HttpStatusCode.Conflict);
            }

            avisosLiberacao = await CancelarPendenciaAsync(new Guiche(dono, estacao), pendencia.Id, cancellationToken);
            pendenciaCancelada = avisosLiberacao is null ? null : pendencia.Id;
        }

        // Raw SQL releases and cancels above: the impersonation act goes in this transaction, naming a cancelled pendência.
        await atos.GravarAsync(pendenciaCancelada, cancellationToken);
        db.SessoesEstacao.Add(dono.Concessao is { } concessaoDona
            ? SessaoEstacao.CriarParaConcessao(estacao.Id, concessaoDona.ConcessaoId, concessaoDona.ExpiraEm)
            : SessaoEstacao.CriarParaSessao(estacao.Id, dono.SessaoId ?? throw new InvalidOperationException("Dono do posto sem sessão nem concessão.")));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: RestricaoEstacaoAtiva,
        })
        {
            // A colleague committed the same guichê between the check above and this insert.
            throw new CustomException(MensagemEscolhidoAgora, [], HttpStatusCode.Conflict);
        }

        if (avisosLiberacao is null)
        {
            await transacao.CommitAsync(cancellationToken);
        }
        else
        {
            await OperacaoGuiche.ConfirmarEAvisarTodosAsync(transacao, avisos, avisosLiberacao, cancellationToken);
        }

        // After the commit: the hub groups follow the links just written (seção 7).
        if (ocupanteSolto?.SessaoId is { } sessaoSolta)
        {
            await conexoes.MoverSessaoParaGuicheAsync(tenantId, sessaoSolta, null, cancellationToken);
        }
        else if (ocupanteSolto?.ConcessaoId is { } concessaoSolta)
        {
            await conexoes.MoverConcessaoParaGuicheAsync(tenantId, concessaoSolta, null, cancellationToken);
        }

        if (dono.Concessao is { } concessaoMovida)
        {
            await conexoes.MoverConcessaoParaGuicheAsync(tenantId, concessaoMovida.ConcessaoId, estacao.Id, cancellationToken);
        }
        else if (dono.SessaoId is { } sessaoId)
        {
            await conexoes.MoverSessaoParaGuicheAsync(tenantId, sessaoId, estacao.Id, cancellationToken);
        }

        return Unit.Value;
    }

    /// <summary>Chamado or EmAtendimento left on the guichê by another user; the caller's own is resumed, not a pendência.</summary>
    internal static Task<Pendencia?> PendenciaDeOutroUsuarioAsync(PafDbContext db, Guid estacaoId, Guid usuarioId, CancellationToken ct) =>
        (from a in db.Atendimentos.AsNoTracking()
         join f in db.Fichas.AsNoTracking() on a.FichaId equals f.Id
         where a.EstacaoId == estacaoId
             && (a.Estado == EstadoAtendimento.Chamado || a.Estado == EstadoAtendimento.EmAtendimento)
             && a.UsuarioId != usuarioId
         select new Pendencia(a.Id, OperacaoGuiche.Senha(f.Letra, f.Numero)))
        .FirstOrDefaultAsync(ct);

    // The RF12 cancel, under the same serialization as the guichê operations: the ficha's rows locked in id order,
    // the state re-read after the lock. Resolved meanwhile by its owner, there is nothing left to release.
    // TravarFichaAsync, not the guichê's TravarAsync: the pendência is another user's by definition, which TravarAsync refuses.
    private async Task<IReadOnlyList<AvisoPaf>?> CancelarPendenciaAsync(Guiche guiche, Guid atendimentoId, CancellationToken ct)
    {
        var atendimento = await OperacaoGuiche.TravarFichaAsync(db, guiche.TenantId, atendimentoId, ct);
        if (atendimento.EstacaoId != guiche.Estacao.Id
            || (atendimento.Estado != EstadoAtendimento.Chamado && atendimento.Estado != EstadoAtendimento.EmAtendimento))
        {
            return null;
        }

        var cancelado = await OperacaoGuiche.CancelarTravadoAsync(db, guiche.TenantId, atendimento, ct);
        return cancelado.Avisos;
    }

    internal sealed record Pendencia(Guid Id, string Senha);

    /// <summary>Releases a dead owner's link (session or impersonation grant) and returns it; a live owner refuses the choice.</summary>
    private async Task<SessaoEstacao> SoltarOcupanteMortoAsync(string tenantId, Estacao estacao, SessaoEstacao ocupante, CancellationToken ct)
    {
        if (ocupante.SessaoId is not { } dono)
        {
            await SoltarConcessaoTerminadaAsync(tenantId, ocupante, ct);
            return ocupante;
        }

        var sessaoDona = await sessoes.GetSessionAsync(dono, ct);
        var viva = sessaoDona is { IsActive: true }
            && await licencas.UnidadeDaMarcaVivaAsync(tenantId, dono, ct) is not null;
        if (viva)
        {
            var nome = sessaoDona!.UserName ?? "outro usuário";
            throw new CustomException(MensagemEmUso(estacao.Nome, nome), [], HttpStatusCode.Conflict);
        }

        await VinculosGuiche.SoltarAsync(db, tenantId, ocupante.Id, ct);
        return ocupante;
    }

    /// <summary>
    /// A grant expired by the exp kept in the link, or ended or revoked in the kit (an unknown grant counts as ended, as
    /// in the kit), no longer holds the guichê: the real user takes it now instead of waiting for the licence sweep.
    /// </summary>
    private async Task SoltarConcessaoTerminadaAsync(string tenantId, SessaoEstacao ocupante, CancellationToken ct)
    {
        var concessaoId = ocupante.ConcessaoId ?? throw new InvalidOperationException("Vínculo de guichê sem sessão nem concessão.");
        var vencida = ocupante.ConcessaoExpiraEm <= relogio.GetUtcNow();
        if (!vencida)
        {
            using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limite.CancelAfter(PrazoConcessao);
            if (!await concessoes.IsRevokedOrEndedAsync(concessaoId.ToString("N"), limite.Token))
            {
                throw new CustomException(MensagemEmUsoPeloSuporte, [], HttpStatusCode.Conflict);
            }
        }

        await VinculosGuiche.SoltarAsync(db, tenantId, ocupante.Id, ct);
    }
}
