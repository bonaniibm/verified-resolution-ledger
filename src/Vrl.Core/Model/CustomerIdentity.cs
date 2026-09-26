using System.Text;

namespace Vrl.Core.Model;

/// <summary>
/// Everything we know about who the customer is. The engine partitions work by <see cref="ResolveKey"/>,
/// so a stable, privacy-safe key matters more than completeness.
/// </summary>
public sealed record CustomerIdentity(
    Guid? ContactId = null,
    Guid? AccountId = null,
    string? Phone = null,
    string? Email = null,
    string? ExternalId = null,
    bool IsAuthenticated = false)
{
    public static CustomerIdentity Anonymous { get; } = new();

    /// <summary>
    /// Already-resolved key, used when rehydrating from the ledger store, which deliberately keeps only the hashed key
    /// (not raw phone/e-mail).
    /// </summary>
    public string? KnownKey { get; init; }
    public IdentityConfidence KnownConfidence { get; init; } = IdentityConfidence.None;

    public static CustomerIdentity FromKnownKey(string? key, IdentityConfidence confidence, Guid? contactId = null, Guid? accountId = null) =>
        new(contactId, accountId) { KnownKey = key, KnownConfidence = key is null ? IdentityConfidence.None : confidence };

    /// <summary>
    /// Resolves the partition key and how much we trust it. Strongest identifier wins.
    /// Phone and e-mail keys are hashed so raw PII never becomes a key or appears in logs.
    /// </summary>
    public (string? Key, IdentityConfidence Confidence) ResolveKey()
    {
        if (KnownKey is not null) return (KnownKey, KnownConfidence);

        if (ContactId is { } c && c != Guid.Empty)
            return ($"contact:{c:N}", IdentityConfidence.High);

        if (!string.IsNullOrWhiteSpace(ExternalId))
            return ($"ext:{Hash(ExternalId.Trim().ToLowerInvariant())}",
                IsAuthenticated ? IdentityConfidence.High : IdentityConfidence.Medium);

        var email = NormalizeEmail(Email);
        if (email is not null)
            return ($"email:{Hash(email)}", IdentityConfidence.Medium);

        var phone = NormalizePhone(Phone);
        if (phone is not null)
            // Shared / household / switchboard numbers make phone-only matching weaker than e-mail.
            return ($"phone:{Hash(phone)}", IdentityConfidence.Medium);

        if (AccountId is { } a && a != Guid.Empty)
            // Account alone is weak: many people call about many issues under one account.
            return ($"account:{a:N}", IdentityConfidence.Low);

        return (null, IdentityConfidence.None);
    }

    public static string? NormalizeEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;
        var e = email.Trim().ToLowerInvariant();
        return e.Contains('@') ? e : null;
    }

    /// <summary>Keeps digits (and a leading '+'). Returns null for implausibly short numbers.</summary>
    public static string? NormalizePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;
        var sb = new StringBuilder();
        foreach (var ch in phone.Trim())
        {
            if (char.IsDigit(ch)) sb.Append(ch);
            else if (ch == '+' && sb.Length == 0) sb.Append(ch);
        }
        if (sb.Length > 0 && sb[0] != '+' && sb.ToString().StartsWith("00")) sb.Remove(0, 2).Insert(0, '+');
        var digits = sb.ToString().TrimStart('+');
        return digits.Length >= 7 ? sb.ToString() : null;
    }

    private static string Hash(string value)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes, 0, 16).ToLowerInvariant();
    }
}
