using System.Security.Cryptography;
using Shent.Core.Crypto;

namespace Shent.Core.Consensus;

/// <summary>
/// Dirección SHENT: Bech32m(hrp de red, [versión 0] + primeros 20 bytes de SHA-256(clave pública Ed25519)).
/// Ejemplo en la red principal: shent1q...
/// </summary>
public sealed class Address : IEquatable<Address>
{
    public const int HashSize = 20;
    public const byte CurrentVersion = 0;

    private readonly byte[] _hash;

    private Address(byte[] hash)
    {
        _hash = hash;
    }

    public ReadOnlySpan<byte> Hash => _hash;

    public static Address FromHash(ReadOnlySpan<byte> hash)
    {
        if (hash.Length != HashSize)
        {
            throw new ArgumentException("La dirección requiere 20 bytes.", nameof(hash));
        }
        return new Address(hash.ToArray());
    }

    public static Address FromPublicKey(ReadOnlySpan<byte> publicKey)
    {
        if (publicKey.Length != Ed25519Keys.PublicKeySize)
        {
            throw new ArgumentException("Clave pública inválida.", nameof(publicKey));
        }
        var digest = SHA256.HashData(publicKey);
        return new Address(digest[..HashSize]);
    }

    public string Encode(string hrp)
    {
        var words = Bech32m.ConvertBits(_hash, 8, 5, pad: true)!;
        var data = new byte[words.Length + 1];
        data[0] = CurrentVersion;
        words.CopyTo(data, 1);
        return Bech32m.Encode(hrp, data);
    }

    public string Encode(NetworkParams network) => Encode(network.AddressHrp);

    public static bool TryParse(string? text, string expectedHrp, out Address? address, out string? error)
    {
        address = null;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "La dirección está vacía.";
            return false;
        }
        if (!Bech32m.TryDecode(text.Trim(), out var hrp, out var data))
        {
            error = "La dirección no es válida: revisá que esté copiada completa y sin errores de escritura.";
            return false;
        }
        if (hrp != expectedHrp)
        {
            error = $"La dirección pertenece a otra red ('{hrp}'); esta red usa '{expectedHrp}'.";
            return false;
        }
        if (data.Length == 0 || data[0] != CurrentVersion)
        {
            error = "Versión de dirección no soportada.";
            return false;
        }
        var bytes = Bech32m.ConvertBits(data.AsSpan(1), 5, 8, pad: false);
        if (bytes is null || bytes.Length != HashSize)
        {
            error = "Longitud de dirección inválida.";
            return false;
        }
        address = new Address(bytes);
        return true;
    }

    public static Address Parse(string text, string expectedHrp)
    {
        if (!TryParse(text, expectedHrp, out var address, out var error))
        {
            throw new FormatException(error);
        }
        return address!;
    }

    public bool Equals(Address? other) => other is not null && _hash.AsSpan().SequenceEqual(other._hash);

    public override bool Equals(object? obj) => obj is Address other && Equals(other);

    public override int GetHashCode() => BitConverter.ToInt32(_hash, 0);

    public string ToHex() => Convert.ToHexString(_hash).ToLowerInvariant();

    public override string ToString() => ToHex();

    public static string Shorten(string encoded) =>
        encoded.Length <= 20 ? encoded : $"{encoded[..12]}…{encoded[^6..]}";
}
