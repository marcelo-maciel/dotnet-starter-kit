using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FSH.Framework.Core.Context;
using FSH.Framework.Core.Exceptions;
using FSH.Framework.Shared.Constants;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Escopo;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Sessao;

/// <summary>
/// Auxiliar único de <see cref="AtoImpersonacao"/> (seção 9, D28): a escrita do PAF feita pela impersonação da Codefi
/// grava uma linha com o operador (<c>act_sub</c>) e a concessão (<c>jti</c>) na mesma transação da escrita, porque a
/// auditoria por requisição do kit grava só o impersonado. Um ato por requisição: chamadas repetidas no mesmo escopo
/// reaproveitam a linha. Sem <c>act_sub</c>, nada é gravado.
/// </summary>
public sealed class AtosImpersonacao(PafDbContext db, ICurrentUser usuarioAtual, IUnidadeCorrente unidadeCorrente)
{
    public const string MensagemSemUnidade = "Impersonação sem unidade corrente não grava no PAF.";
    public const string MensagemCredencialInvalida = "Credencial de impersonação sem operador ou concessão válidos.";

    private AtoImpersonacao? _ato;

    /// <summary>
    /// Puts the act in the change tracker, to be written by the caller's next <c>SaveChanges</c>, inside whatever
    /// transaction is open. A tracker cleared by the caller (an execution strategy retry) gets the same act back.
    /// </summary>
    public void Anotar(Guid? atendimentoId)
    {
        if (DaImpersonacao() is not { } principal)
        {
            return;
        }

        _ato ??= Criar(principal, atendimentoId);

        // The behavior noted the act with no atendimento; a write not yet saved takes the one its handler names now.
        if (atendimentoId is { } id && db.Entry(_ato).State is EntityState.Added or EntityState.Detached)
        {
            _ato.NomearAtendimento(id);
        }

        if (db.Entry(_ato).State == EntityState.Detached)
        {
            db.AtosImpersonacao.Add(_ato);
        }
    }

    /// <summary>
    /// For writes that leave no tracked entity (raw SQL, <c>ExecuteUpdate</c>): writes the act now, inside the caller's
    /// transaction, so a rollback takes the act and the write together. Throws without an open transaction, because
    /// the act would then commit on its own.
    /// </summary>
    public async Task GravarAsync(Guid? atendimentoId, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("AtosImpersonacao.GravarAsync needs the write's open transaction.");
        }

        Anotar(atendimentoId);
        if (_ato is not null && db.Entry(_ato).State == EntityState.Added)
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// For a handler that found its write already done (an idempotent repeat) and leaves without writing: the act the
    /// pipeline noted describes no write, so it leaves the tracker instead of failing the repeat as an unaudited write.
    /// </summary>
    public void Descartar()
    {
        if (_ato is not null)
        {
            db.Entry(_ato).State = EntityState.Detached;
        }

        _ato = null;
    }

    /// <summary>
    /// True when this scope noted an act that never reached the database: the write went around the PAF context, so
    /// the act was lost. The pipeline behavior turns that into an error instead of an unaudited write.
    /// </summary>
    internal bool Pendente => _ato is not null && db.Entry(_ato).State != EntityState.Unchanged;

    private ClaimsPrincipal? DaImpersonacao()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(usuarioAtual.GetUserClaims() ?? []));
        if (!ImpersonacaoPaf.Impersonando(principal))
        {
            return null;
        }

        // The HTTP barrier already refuses the client admin's impersonation; a write reached any other way still
        // fails closed here instead of recording an act the PAF does not accept.
        if (!ImpersonacaoPaf.DaCodefi(principal))
        {
            throw new ForbiddenException(ImpersonacaoPaf.MensagemRecusada);
        }

        return principal;
    }

    private AtoImpersonacao Criar(ClaimsPrincipal principal, Guid? atendimentoId)
    {
        // The grant is keyed by the token jti (ImpersonationGrant.Jti, a Guid), as in the connection registry.
        if (!Guid.TryParse(principal.FindFirstValue(ClaimConstants.ActorSubject), out var operadorId)
            || !Guid.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Jti), out var concessaoId))
        {
            throw new ForbiddenException(MensagemCredencialInvalida);
        }

        var unidadeId = unidadeCorrente.UnidadeId ?? throw new ForbiddenException(MensagemSemUnidade);
        return AtoImpersonacao.Criar(unidadeId, concessaoId, operadorId, atendimentoId);
    }
}
