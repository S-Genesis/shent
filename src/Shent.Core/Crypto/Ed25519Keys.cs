using System.Security.Cryptography;
using NSec.Cryptography;

namespace Shent.Core.Crypto;

/// <summary>
/// Firmas Ed25519 mediante NSec (libsodium). No se implementa criptografía propia:
/// las semillas provienen del CSPRNG del sistema operativo (RandomNumberGenerator de .NET).
/// </summary>
public static class Ed25519Keys
{
    public const int SeedSize = 32;
    public const int PublicKeySize = 32;
    public const int SignatureSize = 64;

    private static readonly SignatureAlgorithm Algorithm = SignatureAlgorithm.Ed25519;

    public static byte[] GenerateSeed() => RandomNumberGenerator.GetBytes(SeedSize);

    /// <summary>Importa una semilla privada. El llamador es responsable de liberar la clave.</summary>
    public static Key ImportSeed(ReadOnlySpan<byte> seed)
    {
        if (seed.Length != SeedSize)
        {
            throw new ArgumentException("La semilla Ed25519 debe tener 32 bytes.", nameof(seed));
        }
        return Key.Import(Algorithm, seed, KeyBlobFormat.RawPrivateKey, new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport,
        });
    }

    public static byte[] ExportSeed(Key key) => key.Export(KeyBlobFormat.RawPrivateKey);

    public static byte[] PublicKeyOf(Key key) => key.PublicKey.Export(KeyBlobFormat.RawPublicKey);

    public static byte[] Sign(Key key, ReadOnlySpan<byte> message) => Algorithm.Sign(key, message);

    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (publicKey.Length != PublicKeySize || signature.Length != SignatureSize)
        {
            return false;
        }
        if (!PublicKey.TryImport(Algorithm, publicKey, KeyBlobFormat.RawPublicKey, out var pk) || pk is null)
        {
            return false;
        }
        return Algorithm.Verify(pk, message, signature);
    }
}
