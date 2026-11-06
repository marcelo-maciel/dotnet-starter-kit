using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace FSH.Modules.PAF.Estacoes;

/// <summary>
/// Link de vínculo e segredo da credencial: 32 bytes aleatórios em base64url, persistidos só como SHA-256.
/// Hash sem sal basta porque o valor já tem 256 bits de entropia; não há dicionário a atacar.
/// </summary>
internal static class SegredoEstacao
{
    public static string Gerar() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string segredo) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(segredo)));
}
