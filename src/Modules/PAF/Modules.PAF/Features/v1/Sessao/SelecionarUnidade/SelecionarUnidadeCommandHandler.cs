using System.Net;
using FSH.Framework.Core.Exceptions;
using FSH.Modules.PAF.Contracts.v1.Sessao;
using FSH.Modules.PAF.Data;
using FSH.Modules.PAF.Escopo;
using FSH.Modules.PAF.Sessao;
using Mediator;
using Microsoft.EntityFrameworkCore;

namespace FSH.Modules.PAF.Features.v1.Sessao.SelecionarUnidade;

/// <summary>
/// Troca a unidade corrente da sessão (ADR 0006). Primeira fatia: a licença fica na unidade do login e não migra.
/// Solta o guichê selecionado no mesmo ato e recusa com atendimento em curso nele; sem Valkey, recusa (seção 9).
/// </summary>
public sealed class SelecionarUnidadeCommandHandler(
    PafDbContext db,
    SessaoCorrente sessao,
    IUnidadeCorrente unidadeCorrente,
    EstadoSessao estado) : ICommandHandler<SelecionarUnidadeCommand>
{
    public const string MensagemForaDoEscopo = "Esta unidade não faz parte do seu acesso.";
    public const string MensagemUnidadeInativa = "Esta unidade está inativa.";
    public const string MensagemTrocaIndisponivel =
        "A troca de unidade está indisponível no momento. Tente novamente em instantes.";

    public async ValueTask<Unit> Handle(SelecionarUnidadeCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var (tenantId, sessaoId, usuarioId) = sessao.Exigir();

        var noEscopo = await db.UsuariosUnidade.AsNoTracking()
            .AnyAsync(u => u.UsuarioId == usuarioId && u.UnidadeId == command.UnidadeId, cancellationToken);
        if (!noEscopo)
        {
            throw new ForbiddenException(MensagemForaDoEscopo);
        }

        var ativa = await db.Unidades.AsNoTracking()
            .Where(u => u.Id == command.UnidadeId)
            .Select(u => u.Ativa)
            .FirstOrDefaultAsync(cancellationToken);
        if (!ativa)
        {
            throw new CustomException(MensagemUnidadeInativa, [], HttpStatusCode.Conflict);
        }

        if (unidadeCorrente.UnidadeId == command.UnidadeId)
        {
            return Unit.Value;
        }

        await using var transacao = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await VinculosGuiche.AtivoDaSessaoAsync(db, sessaoId, cancellationToken) is { } vinculo)
        {
            await VinculosGuiche.SoltarSemAtendimentoEmCursoAsync(db, vinculo, cancellationToken);
        }

        // Valkey is written before the commit: if it fails, the guichê release rolls back with it.
        if (!await estado.DefinirUnidadeAsync(tenantId, sessaoId, command.UnidadeId, cancellationToken))
        {
            throw new CustomException(MensagemTrocaIndisponivel, [], HttpStatusCode.ServiceUnavailable);
        }

        await transacao.CommitAsync(cancellationToken);
        return Unit.Value;
    }
}
