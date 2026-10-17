using System.Globalization;

namespace FSH.Modules.PAF.TempoReal;

/// <summary>
/// SignalR group names of <see cref="PafHub"/> (seção 7). The tenant is part of every key: unit and
/// station ids are not unique across tenants and the Redis backplane is shared by the whole process.
/// </summary>
public static class PafGrupos
{
    public static string Unidade(string tenantId, Guid unidadeId) => Montar(tenantId, "unidade", unidadeId.ToString("D"));

    public static string Painel(string tenantId, Guid estacaoId) => Montar(tenantId, "painel", estacaoId.ToString("D"));

    public static string Guiche(string tenantId, Guid estacaoId) => Montar(tenantId, "guiche", estacaoId.ToString("D"));

    public static string Usuario(string tenantId, string usuarioId) => Montar(tenantId, "usuario", usuarioId);

    private static string Montar(string tenantId, string tipo, string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return string.Create(CultureInfo.InvariantCulture, $"paf:{tenantId}:{tipo}:{id}");
    }
}
