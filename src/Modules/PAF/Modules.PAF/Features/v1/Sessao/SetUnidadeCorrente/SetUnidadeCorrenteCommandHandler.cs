using System.Net;
using FSH.Framework.Core.Exceptions;
using FSH.Modules.PAF.Contracts.v1.Sessao;
using FSH.Modules.PAF.Contracts.v1.TempoReal;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Escopo;
using FSH.Modules.PAF.Features.v1.Atendimentos;
using FSH.Modules.PAF.Sessao;
using FSH.Modules.PAF.TempoReal;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Sessao.SetUnidadeCorrente;

/// <summary>
/// Troca a unidade corrente da sessão (ADR 0006) e migra a licença para o destino na mesma transação (S07, RF22):
/// destino sem contrato vigente ou sem saldo recusa com a sessão intacta. O guichê selecionado é solto só depois da
/// licença aceita, e a troca é recusada com atendimento em curso nele; sem Valkey, recusa (seção 9). Depois do
/// commit, as conexões da sessão mudam de grupo de unidade e o grupo do usuário recebe o aviso.
/// Na impersonação, troca a unidade da concessão dentro do escopo do impersonado, sem licença e sem tocar na
/// sessão nem nas abas do usuário real (RF22).
/// </summary>
public sealed class SetUnidadeCorrenteCommandHandler(
    PafDbContext db,
    SessaoCorrente sessao,
    ConcessaoCorrente concessaoCorrente,
    IUnidadeCorrente unidadeCorrente,
    EstadoSessao estado,
    EscopoUsuario escopo,
    Licencas licencas,
    AvisosTempoReal avisos,
    ConexoesPaf conexoes,
    AtosImpersonacao atos) : ICommandHandler<SetUnidadeCorrenteCommand>
{
    public const string MensagemForaDoEscopo = "Esta unidade não faz parte do seu acesso.";
    public const string MensagemUnidadeInativa = "Esta unidade está inativa.";
    public const string MensagemTrocaIndisponivel =
        "A troca de unidade está indisponível no momento. Tente novamente em instantes.";

    public async ValueTask<Unit> Handle(SetUnidadeCorrenteCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (concessaoCorrente.Valor is { } concessao)
        {
            await TrocarDaConcessaoAsync(concessao, command.UnidadeId, cancellationToken);
            return Unit.Value;
        }

        var (tenantId, sessaoId, usuarioId) = sessao.Exigir();
        if (!await DestinoValidoAsync(usuarioId, command.UnidadeId, cancellationToken))
        {
            return Unit.Value;
        }

        var origem = unidadeCorrente.UnidadeId;
        var aviso = UnidadeCorrenteAlteradaPaf.Criar(
            tenantId, OperacaoGuiche.Correlacao(), usuarioId.ToString(), new UnidadeCorrenteAlterada(Guid.CreateVersion7(), command.UnidadeId));
        var soltouGuiche = false;
        MigracaoLicenca migracao;
        await using (var transacao = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            migracao = await licencas.MigrarAsync(tenantId, usuarioId, sessaoId, command.UnidadeId, cancellationToken);
            try
            {
                // Only after the licence is accepted: a refused switch keeps the guichê.
                if (await VinculosGuiche.AtivoDaSessaoAsync(db, sessaoId, cancellationToken) is { } vinculo)
                {
                    soltouGuiche = await VinculosGuiche.SoltarSemAtendimentoEmCursoAsync(db, tenantId, vinculo, cancellationToken);
                }

                await avisos.GravarAsync(aviso, cancellationToken);

                // Valkey is written before the commit: if it fails, the licence move and the guichê release roll back with it.
                if (!await estado.DefinirUnidadeAsync(tenantId, sessaoId, command.UnidadeId, cancellationToken))
                {
                    throw new CustomException(MensagemTrocaIndisponivel, [], HttpStatusCode.ServiceUnavailable);
                }

                await transacao.CommitAsync(cancellationToken);
            }
            catch
            {
                await licencas.DesfazerMigracaoAsync(migracao);
                throw;
            }
        }

        await licencas.ConcluirMigracaoAsync(migracao, cancellationToken);
        await conexoes.MoverSessaoParaUnidadeAsync(tenantId, sessaoId, origem, command.UnidadeId, cancellationToken);
        if (soltouGuiche)
        {
            await conexoes.MoverSessaoParaGuicheAsync(tenantId, sessaoId, null, cancellationToken);
        }

        await avisos.EnviarAsync(aviso, cancellationToken);
        return Unit.Value;
    }

    // Same rules as the session, on the grant's own link and key: the user's session state is never read or written.
    private async Task TrocarDaConcessaoAsync(ConcessaoImpersonacao concessao, Guid unidadeId, CancellationToken ct)
    {
        if (!await DestinoValidoAsync(concessao.UsuarioId, unidadeId, ct))
        {
            // Already the grant's unit: nothing is written, so the act the pipeline noted describes no write.
            atos.Descartar();
            return;
        }

        await using var transacao = await db.Database.BeginTransactionAsync(ct);
        if (await VinculosGuiche.AtivoDaConcessaoAsync(db, concessao.ConcessaoId, ct) is { } vinculo)
        {
            await VinculosGuiche.SoltarSemAtendimentoEmCursoAsync(db, concessao.TenantId, vinculo, ct);
        }

        // S11: the grant's guichê release is raw SQL, so its impersonation act is written here, in the same transaction.
        await atos.GravarAsync(atendimentoId: null, ct);

        if (!await estado.DefinirUnidadeDaConcessaoAsync(concessao.TenantId, concessao.ConcessaoId, unidadeId, concessao.ExpiraEm, ct))
        {
            throw new CustomException(MensagemTrocaIndisponivel, [], HttpStatusCode.ServiceUnavailable);
        }

        await transacao.CommitAsync(ct);
    }

    /// <summary>Falso when the unidade is already the current one (nothing to do); throws when it is refused.</summary>
    private async Task<bool> DestinoValidoAsync(Guid usuarioId, Guid unidadeId, CancellationToken ct)
    {
        if (!await escopo.ContemAsync(usuarioId, unidadeId, ct))
        {
            throw new ForbiddenException(MensagemForaDoEscopo);
        }

        var ativa = await db.Unidades.AsNoTracking()
            .Where(u => u.Id == unidadeId)
            .Select(u => u.Ativa)
            .FirstOrDefaultAsync(ct);
        if (!ativa)
        {
            throw new CustomException(MensagemUnidadeInativa, [], HttpStatusCode.Conflict);
        }

        return unidadeCorrente.UnidadeId != unidadeId;
    }
}
