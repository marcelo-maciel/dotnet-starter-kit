namespace FSH.Modules.Identity.Services;

/// <summary>
/// Whether two stored or typed e-mail addresses are the same mailbox for the lookups that cross tenants.
/// The normalized e-mail (NFC plus ToUpperInvariant) finds the candidates but folds more than ASCII case:
/// the long s (U+017F) upper-cases to S and NFC turns the Kelvin sign (U+212A) into K, so an Admin of one
/// tenant could create <c>ſusan@corp.com</c> and have it taken for <c>susan@corp.com</c> of another tenant.
/// Here only ASCII letters compare without case and every other character must be identical, with no
/// Unicode normalization: even NFC folds that look-alike. The typed address reaches this already in NFC.
/// </summary>
internal static class EmailAddressMatch
{
    public static bool IsSameAddress(string? left, string? right)
    {
        if (left is null || right is null || left.Length != right.Length)
        {
            return false;
        }

        for (int i = 0; i < left.Length; i++)
        {
            char a = left[i];
            char b = right[i];
            if (a != b && !(char.IsAsciiLetter(a) && char.IsAsciiLetter(b) && (a | 0x20) == (b | 0x20)))
            {
                return false;
            }
        }

        return true;
    }
}
