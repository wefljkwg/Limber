using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Limber.Koi.Sync;

/// <summary>
/// A join code: where the inviter's relay is, which invite room, the password for that room, and the
/// end-to-end encryption key. Only the two players ever have the key - relays only see encrypted bytes.
/// Format: "LMB1-" + base64url( 1 | hostLen | host | room(16) | auth(16) | key(32) ).
/// </summary>
public sealed class JoinCode
{
    public const string Prefix = "LMB1-";

    public string RelayHost { get; }
    public byte[] Room { get; }
    public byte[] Auth { get; }
    public byte[] Key { get; }

    public string RoomHex => Convert.ToHexString(Room).ToLowerInvariant();
    public string AuthHex => Convert.ToHexString(Auth).ToLowerInvariant();
    public string AuthHashHex => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(AuthHex))).ToLowerInvariant();
    public string RelayUrl => "https://" + RelayHost;

    private JoinCode(string host, byte[] room, byte[] auth, byte[] key)
    {
        RelayHost = host;
        Room = room;
        Auth = auth;
        Key = key;
    }

    public static JoinCode Create(string relayUrl)
    {
        var host = new Uri(relayUrl).Host;
        return new JoinCode(host, RandomNumberGenerator.GetBytes(16), RandomNumberGenerator.GetBytes(16), RandomNumberGenerator.GetBytes(32));
    }

    public override string ToString()
    {
        using var ms = new MemoryStream();
        var hostBytes = Encoding.ASCII.GetBytes(RelayHost);
        ms.WriteByte(1);
        ms.WriteByte((byte)hostBytes.Length);
        ms.Write(hostBytes);
        ms.Write(Room);
        ms.Write(Auth);
        ms.Write(Key);
        return Prefix + Convert.ToBase64String(ms.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool TryParse(string text, out JoinCode? code, out string error)
    {
        code = null;
        error = "";
        text = (text ?? "").Trim();
        if (!text.StartsWith(Prefix, StringComparison.Ordinal))
        {
            error = "That isn't a Limber sync code (they start with LMB1-).";
            return false;
        }
        try
        {
            var b64 = text[Prefix.Length..].Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            var data = Convert.FromBase64String(b64);
            int hostLen = data.Length > 2 ? data[1] : 0;
            if (data.Length != 2 + hostLen + 64 || data[0] != 1)
                throw new FormatException();
            var host = Encoding.ASCII.GetString(data, 2, hostLen);
            if (!host.EndsWith(".workers.dev", StringComparison.OrdinalIgnoreCase) || Uri.CheckHostName(host) != UriHostNameType.Dns)
                throw new FormatException();
            int o = 2 + hostLen;
            code = new JoinCode(host, data[o..(o + 16)], data[(o + 16)..(o + 32)], data[(o + 32)..(o + 64)]);
            return true;
        }
        catch (Exception)
        {
            error = "That code is incomplete or damaged. Ask for it again and copy all of it.";
            return false;
        }
    }
}

/// <summary>AES-256-GCM sealing of every message between the two players.</summary>
public sealed class SyncCrypto : IDisposable
{
    private static readonly byte[] Associated = Encoding.ASCII.GetBytes("Limber-sync-v1");
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly AesGcm _aes;

    public SyncCrypto(byte[] key) => _aes = new AesGcm(key, TagSize);

    public byte[] Seal(ReadOnlySpan<byte> plain)
    {
        var output = new byte[NonceSize + plain.Length + TagSize];
        var nonce = output.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        _aes.Encrypt(nonce, plain, output.AsSpan(NonceSize, plain.Length), output.AsSpan(NonceSize + plain.Length, TagSize), Associated);
        return output;
    }

    /// <summary>Null if the message was tampered with or encrypted with a different key.</summary>
    public byte[]? Open(ReadOnlySpan<byte> sealedMessage)
    {
        if (sealedMessage.Length < NonceSize + TagSize)
            return null;
        var length = sealedMessage.Length - NonceSize - TagSize;
        var plain = new byte[length];
        try
        {
            _aes.Decrypt(sealedMessage[..NonceSize], sealedMessage.Slice(NonceSize, length),
                sealedMessage.Slice(NonceSize + length, TagSize), plain, Associated);
            return plain;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public void Dispose() => _aes.Dispose();
}
