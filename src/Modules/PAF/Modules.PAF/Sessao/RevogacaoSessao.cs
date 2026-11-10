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
    public const string MensagemUsuarioInativo = "Seu usuário foi desativado. Procure o administrador da empresa.";
    public const string MotivoSenhaDefinida = "PAF: senha definida pelo administrador";
    public const string MensagemSenhaDefinida = "Sua senha foi alterada pelo administrador. Entre com a senha nova.";

    public async Task RevogarAsync(string tenantId, Guid sessaoId, Guid usuarioId, string motivo, CancellationToken ct)
    {
        await licencas.LiberarAsync(tenantId, sessaoId, ct);
        await estado.RemoverAsync(tenantId, sessaoId, ct);
        await sessoes.RevokeSessionForAdminAsync(sessaoId, usuarioId.ToString(), motivo, ct);
        await conexoes.EncerrarSessaoAsync(tenantId, sessaoId, MensagemDaTela(motivo), ct);
    }

    // The kit reason is for the session audit; the screen gets a sentence the operator can act on (SessaoInvalidada).
    // A reason added later without its own sentence still closes the screen, with the generic one.
    private static string MensagemDaTela(string motivo) => motivo switch
    {
        MotivoUnidadeRetirada => MensagemUnidadeRetirada,
        MotivoUsuarioInativo => MensagemUsuarioInativo,
        MotivoSenhaDefinida => MensagemSenhaDefinida,
        _ => SessaoRevogadaPafHandler.MensagemSessaoEncerrada,
    };
}
