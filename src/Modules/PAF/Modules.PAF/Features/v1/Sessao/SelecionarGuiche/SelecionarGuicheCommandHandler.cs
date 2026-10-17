using System.Net;
using FSH.Framework.Core.Exceptions;
using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.PAF.Contracts.v1.Sessao;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Domain;
using FSH.Modules.PAF.Sessao;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FSH.Modules.PAF.Features.v1.Sessao.SelecionarGuiche;

/// <summary>
/// Seleção do guichê depois do login (ADR 0006, seção 5): um guichê por sessão e uma sessão por guichê. Trocar de
/// guichê solta o atual na mesma transação e é recusado com atendimento em curso nele. Vínculo de sessão morta é
/// solto e a escolha aceita no mesmo ato; o de sessão viva recusa informando quem está no guichê.
/// </summary>
public sealed class SelecionarGuicheCommandHandler(
    PafDbContext db,
    SessaoCorrente sessao,
    ISessionService sessoes,
    Licencas licencas) : ICommandHandler<SelecionarGuicheCommand>
{
    public const string RestricaoEstacaoAtiva = "UX_SessoesEstacao_EstacaoAtiva";
    public const string MensagemNaoEncontrado = "Guichê não encontrado na unidade corrente.";
    public const string MensagemNaoEGuiche = "Esta estação não é um guichê ativo.";
    public const string MensagemEmUsoPeloSuporte = "O guichê está em uso pelo suporte.";
    public const string MensagemAtendimentoEmCurso = VinculosGuiche.MensagemAtendimentoEmCurso;
    public const string MensagemEscolhidoAgora = "O guichê acabou de ser escolhido por outra pessoa.";

    private static readonly TipoEstacao[] TiposComGuiche = [TipoEstacao.Guiche, TipoEstacao.EmissaoGuiche, TipoEstacao.GuichePainel];

    public static string MensagemEmUso(string nomeEstacao, string ocupante) =>
        $"O guichê {nomeEstacao} está em uso por {ocupante}.";

    public async ValueTask<Unit> Handle(SelecionarGuicheCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var (tenantId, sessaoId, _) = sessao.Exigir();

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
            await VinculosGuiche.SoltarSemAtendimentoEmCursoAsync(db, atual, cancellationToken);
        }

        if (await VinculosGuiche.AtivoDaEstacaoAsync(db, estacao.Id, cancellationToken) is { } ocupante)
        {
            await SoltarOcupanteMortoAsync(tenantId, estacao, ocupante, cancellationToken);
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

        await transacao.CommitAsync(cancellationToken);
        return Unit.Value;
    }

    private async Task SoltarOcupanteMortoAsync(string tenantId, Estacao estacao, SessaoEstacao ocupante, CancellationToken ct)
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

        await VinculosGuiche.SoltarAsync(db, ocupante.Id, ct);
    }
}
