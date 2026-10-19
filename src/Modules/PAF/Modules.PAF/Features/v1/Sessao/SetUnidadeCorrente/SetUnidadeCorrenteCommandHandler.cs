using System.Net;
using FSH.Framework.Core.Exceptions;
using FSH.Modules.PAF.Contracts.v1.Sessao;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Escopo;
using FSH.Modules.PAF.Sessao;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Sessao.SetUnidadeCorrente;

/// <summary>
/// Troca a unidade corrente da sessão (ADR 0006). Primeira fatia: a licença fica na unidade do login e não migra.
/// Solta o guichê selecionado no mesmo ato e recusa com atendimento em curso nele; sem Valkey, recusa (seção 9).
/// Na impersonação, troca a unidade da concessão dentro do escopo do impersonado, sem licença e sem tocar na
/// sessão nem nas abas do usuário real (RF22).
/// </summary>
public sealed class SetUnidadeCorrenteCommandHandler(
    PafDbContext db,
    SessaoCorrente sessao,
    ConcessaoCorrente concessaoCorrente,
    IUnidadeCorrente unidadeCorrente,
    EstadoSessao estado,
    EscopoUsuario escopo) : ICommandHandler<SetUnidadeCorrenteCommand>
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

        await using var transacao = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await VinculosGuiche.AtivoDaSessaoAsync(db, sessaoId, cancellationToken) is { } vinculo)
        {
            await VinculosGuiche.SoltarSemAtendimentoEmCursoAsync(db, tenantId, vinculo, cancellationToken);
        }

        // Valkey is written before the commit: if it fails, the guichê release rolls back with it.
        if (!await estado.DefinirUnidadeAsync(tenantId, sessaoId, command.UnidadeId, cancellationToken))
        {
            throw new CustomException(MensagemTrocaIndisponivel, [], HttpStatusCode.ServiceUnavailable);
        }

        await transacao.CommitAsync(cancellationToken);
        return Unit.Value;
    }

    // Same rules as the session, on the grant's own link and key: the user's session state is never read or written.
    private async Task TrocarDaConcessaoAsync(ConcessaoImpersonacao concessao, Guid unidadeId, CancellationToken ct)
    {
        if (!await DestinoValidoAsync(concessao.UsuarioId, unidadeId, ct))
        {
            return;
        }

        await using var transacao = await db.Database.BeginTransactionAsync(ct);
        if (await VinculosGuiche.AtivoDaConcessaoAsync(db, concessao.ConcessaoId, ct) is { } vinculo)
        {
            await VinculosGuiche.SoltarSemAtendimentoEmCursoAsync(db, concessao.TenantId, vinculo, ct);
        }

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
