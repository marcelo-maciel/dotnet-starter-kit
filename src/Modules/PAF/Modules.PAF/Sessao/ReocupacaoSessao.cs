using FSH.Framework.Core.Exceptions;
using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.PAF.Contracts.v1.TempoReal;
using FSH.Modules.PAF.Features.v1.Atendimentos;
using FSH.Modules.PAF.TempoReal;

namespace FSH.Modules.PAF.Sessao;

/// <summary>
/// Ocupação da sessão que chega com token de acesso ainda válido depois de a licença ter sido solta (carência de
/// reconexão, marca vencida): a requisição REST e a conexão ao PafHub nunca passam pelo pipeline da renovação, então
/// passam por esta, com a mesma cascata e a mesma recusa do login (ARQ seção 9, sessão revivida: qualquer caminho que revive a sessão
/// passa pela ocupação). Recusa é <see cref="UnauthorizedException"/> com a mensagem da causa.
/// </summary>
public sealed class ReocupacaoSessao(
    Licencas licencas,
    ISessionService sessoes,
    EstadoSessao estado,
    AvisosTempoReal avisos)
{
    /// <summary>
    /// Unidade da licença viva da sessão, ocupando-a pela cascata quando não há marca viva. Nulo quando o usuário não
    /// é do PAF (sem <c>UsuarioUnidade</c>): ele não ocupa licença e o filtro de unidade continua fechado para ele.
    /// </summary>
    public Task<Guid?> GarantirAsync(string tenantId, Guid sessaoId, Guid usuarioId, CancellationToken ct) =>
        ComoNaoAutorizadoAsync(() => OcuparAsync(tenantId, sessaoId, usuarioId, ct));

    private async Task<Guid?> OcuparAsync(string tenantId, Guid sessaoId, Guid usuarioId, CancellationToken ct)
    {
        if (await licencas.UnidadeDaMarcaVivaAsync(tenantId, sessaoId, ct) is { } viva)
        {
            return viva;
        }

        if (!await licencas.UsuarioDoPafAsync(usuarioId, ct))
        {
            return null;
        }

        var unidadeAnterior = await estado.LerUnidadeAsync(tenantId, sessaoId, ct)
            ?? await licencas.UnidadeDaUltimaMarcaAsync(tenantId, sessaoId, ct);

        UnidadeCorrenteAlteradaPaf? aviso = null;
        Guid unidadeOcupada;
        await using (var reserva = await licencas.ReservarAsync(tenantId, usuarioId, ct))
        {
            // A PAF user whose effective scope is empty (escopo empresa without a principal row) has no unit to work in.
            if (reserva is null)
            {
                throw new UnauthorizedException(Licencas.MensagemSemUnidadeAtiva);
            }

            // Under the user lock taken by the reservation: a REST request and a hub reconnection of the same session
            // arriving together must occupy once; the second one finds the mark the first one committed.
            if (await licencas.UnidadeDaMarcaVivaAsync(tenantId, sessaoId, ct) is { } ocupadaNoIntervalo)
            {
                return ocupadaNoIntervalo;
            }

            // A login on another machine may have revoked this session while it waited on the lock; confirming now
            // would delete the new session's row (sessão única), as in RefreshTokenLicenseBehavior.
            if (await sessoes.GetSessionAsync(sessaoId, ct) is not { IsActive: true })
            {
                throw new UnauthorizedException(SessionCheckMiddleware.MensagemSessaoInvalida);
            }

            if (unidadeAnterior is { } anterior && anterior != reserva.UnidadeId)
            {
                aviso = UnidadeCorrenteAlteradaPaf.Criar(
                    tenantId, OperacaoGuiche.Correlacao(), usuarioId.ToString(), new UnidadeCorrenteAlterada(Guid.CreateVersion7(), reserva.UnidadeId));
                await avisos.GravarAsync(aviso, ct);
            }

            await reserva.ConfirmarAsync(sessaoId, [], ct);
            unidadeOcupada = reserva.UnidadeId;
        }

        if (aviso is not null)
        {
            // The unit chosen before (manual switch) would override the licence unit in SessionCheckMiddleware.
            await estado.RemoverAsync(tenantId, sessaoId, ct);
            await avisos.EnviarAsync(aviso, ct);
        }

        return await licencas.UnidadeDaMarcaVivaAsync(tenantId, sessaoId, ct) ?? unidadeOcupada;
    }

    // The cascade refuses with 403 on the login; on a request that already carries a token the same refusal is a 401,
    // so the screen shows the cause and goes back to the login (ARQ seção 9). Reservation and confirmation both refuse.
    private static async Task<T> ComoNaoAutorizadoAsync<T>(Func<Task<T>> ocupacao)
    {
        try
        {
            return await ocupacao();
        }
        catch (ForbiddenException ex)
        {
            throw new UnauthorizedException(ex.Message);
        }
    }
}
