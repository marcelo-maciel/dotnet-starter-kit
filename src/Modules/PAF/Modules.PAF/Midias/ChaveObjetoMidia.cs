using System.Text.RegularExpressions;

namespace FSH.Modules.PAF.Midias;

/// <summary>Um envio de mídia: o identificador novo e a chave do objeto que só ele usa.</summary>
public sealed record EnvioChave(Guid EnvioId, string Chave);

/// <summary>
/// Chave do objeto de mídia no armazenamento do kit (ADR 0007): <c>paf-midia/{tenantId}/{unidadeId}/{envioId:N}.{ext}</c>.
/// Cada envio ganha um identificador v7 novo, então a chave nunca é reaproveitada e trocar um vídeo nunca
/// sobrescreve objeto sob o bloqueio de 30 dias. A raiz fica fora de <c>public/</c>, que as políticas de bucket do kit
/// abrem para leitura anônima. O <c>Storage:S3:Prefix</c> não entra aqui: o provedor S3 do kit o acrescenta na frente
/// de toda chave, e <see cref="OpcoesMidia"/> recusa na inicialização o prefixo que colidiria com esta raiz.
/// </summary>
public static partial class ChaveObjetoMidia
{
    public const string Raiz = "paf-midia/";

    // The kit does not constrain the tenant id (CreateTenant only checks it is not empty), and it is the one
    // free-text segment of the key: anything beyond this set could climb out of the unit's folder.
    // \A and \z, not ^ and $: $ also matches before a trailing newline.
    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9_-]{0,63}\z")]
    private static partial Regex TenantSeguro();

    public static EnvioChave NovoEnvio(string tenantId, Guid unidadeId, FormatoMidia formato)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(formato);

        if (!TenantSeguro().IsMatch(tenantId))
        {
            throw new ArgumentException("Tenant fora do formato aceito na chave da mídia.", nameof(tenantId));
        }

        if (unidadeId == Guid.Empty)
        {
            throw new ArgumentException("Unidade vazia na chave da mídia.", nameof(unidadeId));
        }

        // The extension comes from the format table, never from the uploaded name.
        var envioId = Guid.CreateVersion7();
        return new EnvioChave(envioId, $"{Raiz}{tenantId}/{unidadeId:D}/{envioId:N}{formato.Extensao}");
    }
}
