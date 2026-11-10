using FSH.Modules.PAF.Domain;

namespace FSH.Modules.PAF.Sessao;

/// <summary>
/// Dono do posto nesta requisição (seção 9, S11): a sessão do kit do atendente ou, sob a impersonação da Codefi, a
/// concessão, que tem vínculo de guichê próprio (<c>SessaoEstacao.ConcessaoId</c>) e nunca toca no da sessão do usuário
/// real. <see cref="UsuarioId"/> é o <c>sub</c> nos dois casos: o atendimento é gravado em nome do impersonado.
/// </summary>
internal sealed record DonoPosto(string TenantId, Guid UsuarioId, Guid? SessaoId, ConcessaoImpersonacao? Concessao)
{
    /// <summary>The grant when the middleware set one, else the person's session; neither (root, station) is 403.</summary>
    public static DonoPosto Exigir(SessaoCorrente sessao, ConcessaoCorrente concessao)
    {
        if (concessao.Valor is { } c)
        {
            return new DonoPosto(c.TenantId, c.UsuarioId, SessaoId: null, c);
        }

        var (tenantId, sessaoId, usuarioId) = sessao.Exigir();
        return new DonoPosto(tenantId, usuarioId, sessaoId, Concessao: null);
    }

    /// <summary>True when <paramref name="vinculo"/> belongs to this owner.</summary>
    public bool Possui(SessaoEstacao vinculo) =>
        Concessao is { } c ? vinculo.ConcessaoId == c.ConcessaoId : vinculo.SessaoId == SessaoId;
}
