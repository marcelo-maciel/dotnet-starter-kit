using FSH.Modules.Identity.Contracts.Services;
using FSH.Modules.PAF.TempoReal;

namespace FSH.Modules.PAF.Sessao;

/// <summary>
/// Revogação que nasce no PAF (seção 9): solta licença e guichê, apaga a unidade corrente no Valkey e revoga o
/// <c>UserSession</c>, no mesmo ato. Soltar vem antes de revogar, como no logout: se a revogação falhar, a sessão
/// sem marca viva já é recusada na requisição seguinte. As conexões do PafHub caem depois da revogação, para a
/// reconexão já encontrar a sessão morta.
/// </summary>
public sealed class RevogacaoSessao(Licencas licencas, EstadoSessao estado, ISessionService sessoes, ConexoesPaf conexoes)
{
    public const string MensagemUnidadeRetirada = "A unidade da sessão saiu do seu acesso. Entre novamente.";
    public const string MotivoUnidadeRetirada = "PAF: unidade corrente retirada do escopo";
    public const string MotivoUsuarioInativo = "PAF: usuário desativado";

    public async Task RevogarAsync(string tenantId, Guid sessaoId, Guid usuarioId, string motivo, CancellationToken ct)
    {
        await licencas.LiberarAsync(tenantId, sessaoId, ct);
        await estado.RemoverAsync(tenantId, sessaoId, ct);
        await sessoes.RevokeSessionForAdminAsync(sessaoId, usuarioId.ToString(), motivo, ct);
        await conexoes.EncerrarSessaoAsync(tenantId, sessaoId, ct);
    }
}
