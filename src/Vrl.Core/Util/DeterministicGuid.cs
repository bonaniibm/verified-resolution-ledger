using System.Security.Cryptography;
using System.Text;

namespace Vrl.Core.Util;

/// <summary>
/// RFC 4122 version-5 (SHA-1, name-based) GUIDs. Used so that ledger rows get the same primary key every time the
/// same source record or episode is processed – upserts become naturally idempotent and replay-safe.
/// </summary>
public static class DeterministicGuid
{
    /// <summary>Namespace for all Verified Resolution Ledger identifiers.</summary>
    public static readonly Guid LedgerNamespace = Guid.Parse("6f1c1e5a-2b0e-4d8a-9a57-3c6e1f0b7d21");

    public static Guid Create(Guid namespaceId, string name)
    {
        var nsBytes = namespaceId.ToByteArray();
        SwapByteOrder(nsBytes);
        var nameBytes = Encoding.UTF8.GetBytes(name);

        var data = new byte[nsBytes.Length + nameBytes.Length];
        Buffer.BlockCopy(nsBytes, 0, data, 0, nsBytes.Length);
        Buffer.BlockCopy(nameBytes, 0, data, nsBytes.Length, nameBytes.Length);

#pragma warning disable CA5350 // SHA-1 is mandated by RFC 4122 v5; not used for security.
        var hash = SHA1.HashData(data);
#pragma warning restore CA5350

        var guid = new byte[16];
        Array.Copy(hash, guid, 16);
        guid[6] = (byte)((guid[6] & 0x0F) | 0x50);
        guid[8] = (byte)((guid[8] & 0x3F) | 0x80);
        SwapByteOrder(guid);
        return new Guid(guid);
    }

    public static Guid ForInteraction(string sourceSystem, string sourceRecordId) =>
        Create(LedgerNamespace, $"interaction|{sourceSystem.ToLowerInvariant()}|{sourceRecordId.ToLowerInvariant()}");

    public static Guid ForEpisode(string episodeKey) => Create(LedgerNamespace, $"episode|{episodeKey}");

    private static void SwapByteOrder(byte[] g)
    {
        (g[0], g[3]) = (g[3], g[0]);
        (g[1], g[2]) = (g[2], g[1]);
        (g[4], g[5]) = (g[5], g[4]);
        (g[6], g[7]) = (g[7], g[6]);
    }
}
