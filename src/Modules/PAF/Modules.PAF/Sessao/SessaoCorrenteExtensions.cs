using FSH.Framework.Core.Exceptions;

namespace FSH.Modules.PAF.Sessao;

internal static class SessaoCorrenteExtensions
{
    public const string MensagemSemSessaoPaf = "Operação exclusiva de usuário com unidade no PAF.";

    /// <summary>Sessão conferida pelo middleware; recusa quem não ocupa licença no PAF (raiz, impersonação, estação).</summary>
    public static (string TenantId, Guid SessaoId, Guid UsuarioId) Exigir(this SessaoCorrente sessao) =>
        sessao is { TenantId: { } tenantId, SessaoId: { } sessaoId, UsuarioId: { } usuarioId }
            ? (tenantId, sessaoId, usuarioId)
            : throw new ForbiddenException(MensagemSemSessaoPaf);
}
