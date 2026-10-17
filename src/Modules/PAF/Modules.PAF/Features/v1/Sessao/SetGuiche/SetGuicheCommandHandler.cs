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
    ISessionService sessoes,
    Licencas licencas,
    AvisosTempoReal avisos,
    ConexoesPaf conexoes) : ICommandHandler<SetGuicheCommand>
{
    public const string RestricaoEstacaoAtiva = "UX_SessoesEstacao_EstacaoAtiva";
    public const string MensagemNaoEncontrado = "Guichê não encontrado na unidade corrente.";
    public const string MensagemNaoEGuiche = "Esta estação não é um guichê ativo.";
    public const string MensagemEmUsoPeloSuporte = "O guichê está em uso pelo suporte.";
    public const string MensagemAtendimentoEmCurso = VinculosGuiche.MensagemAtendimentoEmCurso;
    public const string MensagemEscolhidoAgora = "O guichê acabou de ser escolhido por outra pessoa.";

    internal static readonly TipoEstacao[] TiposComGuiche = [TipoEstacao.Guiche, TipoEstacao.EmissaoGuiche, TipoEstacao.GuichePainel];

    public static string MensagemEmUso(string nomeEstacao, string ocupante) =>
        $"O guichê {nomeEstacao} está em uso por {ocupante}.";

    public static string MensagemPendencia(string nomeEstacao, string senha) =>
        $"O guichê {nomeEstacao} tem a senha {senha} pendente de outro atendente. Libere a pendência para usar este guichê.";

    public async ValueTask<Unit> Handle(SetGuicheCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var (tenantId, sessaoId, usuarioId) = sessao.Exigir();

        // The unit filter limits the choice to the current unit's stations (ADR 0006).
        var estacao = await db.Estacoes.AsNoTracking().FirstOrDefaultAsync(e => e.Id == command.EstacaoId, cancellationToken)
            ?? throw new NotFoundException(MensagemNaoEncontrado);
        if (!estacao.Ativa || !TiposComGuiche.Contains(estacao.Tipo))
        {
            throw new CustomException(MensagemNaoEGuiche, [], HttpStatusCode.Conflict);
        }

        await using var transacao = await db.Database.BeginTransactionAsync(cancellationToken);
        var atual = await VinculosGuiche.AtivoDaSessaoAsync(db, sessaoId, cancellationToken);
        if (atual?.EstacaoId == estacao.Id)
        {
            return Unit.Value;
        }

        if (atual is not null)
        {
            await VinculosGuiche.SoltarSemAtendimentoEmCursoAsync(db, tenantId, atual, cancellationToken);
        }

        Guid? ocupanteSolto = null;
        if (await VinculosGuiche.AtivoDaEstacaoAsync(db, estacao.Id, cancellationToken) is { } ocupante)
        {
            ocupanteSolto = await SoltarOcupanteMortoAsync(tenantId, estacao, ocupante, cancellationToken);
        }

        AtendimentoAtualizadoPaf? avisoLiberacao = null;
        if (await PendenciaDeOutroUsuarioAsync(db, estacao.Id, usuarioId, cancellationToken) is { } pendencia)
        {
            if (!command.LiberarPendencia)
            {
                throw new CustomException(MensagemPendencia(estacao.Nome, pendencia.Senha), [], HttpStatusCode.Conflict);
            }

            avisoLiberacao = await CancelarPendenciaAsync(new Guiche(tenantId, sessaoId, usuarioId, estacao), pendencia.Id, cancellationToken);
        }

        db.SessoesEstacao.Add(SessaoEstacao.CriarParaSessao(estacao.Id, sessaoId));
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

        if (avisoLiberacao is null)
        {
            await transacao.CommitAsync(cancellationToken);
        }
        else
        {
            await OperacaoGuiche.ConfirmarEAvisarAsync(transacao, avisos, avisoLiberacao, cancellationToken);
        }

        // After the commit: the hub groups follow the links just written (seção 7).
        if (ocupanteSolto is { } solto)
        {
            await conexoes.MoverSessaoParaGuicheAsync(tenantId, solto, null, cancellationToken);
        }

        await conexoes.MoverSessaoParaGuicheAsync(tenantId, sessaoId, estacao.Id, cancellationToken);

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
    private async Task<AtendimentoAtualizadoPaf?> CancelarPendenciaAsync(Guiche guiche, Guid atendimentoId, CancellationToken ct)
    {
        var atendimento = await OperacaoGuiche.TravarAsync(db, guiche, atendimentoId, ct);
        if (atendimento.EstacaoId != guiche.Estacao.Id
            || (atendimento.Estado != EstadoAtendimento.Chamado && atendimento.Estado != EstadoAtendimento.EmAtendimento))
        {
            return null;
        }

        var versao = await OperacaoGuiche.TransitarAsync(db, atendimento, $"""
            UPDATE paf."Atendimentos"
               SET "Estado" = 'Cancelado', "CanceladoEm" = now(), "Versao" = "Versao" + 1
             WHERE "TenantId" = {guiche.TenantId} AND "Id" = {atendimento.Id} AND "Estado" IN ('Chamado', 'EmAtendimento')
            RETURNING "Versao" AS "Value"
            """, ct);
        await OperacaoGuiche.LiberarPendentesAsync(db, guiche, atendimento, ct);
        return OperacaoGuiche.AvisoDeEstado(guiche, atendimento, versao, EstadoAtendimento.Cancelado);
    }

    internal sealed record Pendencia(Guid Id, string Senha);

    /// <summary>Releases a dead session's link and returns that session; a live owner refuses the choice.</summary>
    private async Task<Guid> SoltarOcupanteMortoAsync(string tenantId, Estacao estacao, SessaoEstacao ocupante, CancellationToken ct)
    {
        // ponytail: a grant-owned link follows the grant's life (impersonation slice); refused here until then.
        if (ocupante.SessaoId is not { } dono)
        {
            throw new CustomException(MensagemEmUsoPeloSuporte, [], HttpStatusCode.Conflict);
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
        return dono;
    }
}
