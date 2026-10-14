using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NSec.Cryptography;
using Shent.Core.Consensus;
using Shent.Core.Crypto;

namespace Shent.Core.Wallet;

public sealed class WalletFormatException(string message) : Exception(message);

/// <summary>Error genérico de descifrado: no distingue entre contraseña incorrecta y archivo alterado.</summary>
public sealed class WalletPasswordException() : Exception("Contraseña incorrecta o archivo dañado.");

public sealed record KdfParameters
{
    [JsonPropertyName("name")] public string Name { get; init; } = "argon2id";
    [JsonPropertyName("version")] public int Version { get; init; } = 19;
    [JsonPropertyName("memoryKiB")] public int MemoryKiB { get; init; } = 131072;
    [JsonPropertyName("iterations")] public int Iterations { get; init; } = 3;
    [JsonPropertyName("parallelism")] public int Parallelism { get; init; } = 1;
    [JsonPropertyName("salt")] public string Salt { get; init; } = string.Empty;
}

public sealed record CipherParameters
{
    [JsonPropertyName("name")] public string Name { get; init; } = "chacha20-poly1305";
    [JsonPropertyName("nonce")] public string Nonce { get; init; } = string.Empty;
    [JsonPropertyName("ciphertext")] public string Ciphertext { get; init; } = string.Empty;
    [JsonPropertyName("tag")] public string Tag { get; init; } = string.Empty;
}

/// <summary>
/// Respaldo portable "wallet.shent" (JSON versionado). La semilla Ed25519 se cifra con ChaCha20-Poly1305
/// usando una clave derivada de la contraseña maestra con Argon2id. Todos los metadatos (formato, red,
/// dirección, seudónimo y parámetros KDF) se autentican como datos asociados: cualquier alteración
/// hace fallar el descifrado.
/// </summary>
public sealed record WalletFile
{
    public const string FormatId = "shent-wallet";
    public const int CurrentVersion = 1;
    public const string Extension = ".shent";

    public const int MinMemoryKiB = 19_456;
    public const int MaxMemoryKiB = 1_048_576;
    public const int MaxIterations = 10;

    [JsonPropertyName("format")] public string Format { get; init; } = FormatId;
    [JsonPropertyName("version")] public int Version { get; init; } = CurrentVersion;
    [JsonPropertyName("network")] public string Network { get; init; } = string.Empty;
    [JsonPropertyName("address")] public string Address { get; init; } = string.Empty;
    [JsonPropertyName("pseudonym")] public string Pseudonym { get; init; } = string.Empty;
    [JsonPropertyName("label")] public string Label { get; init; } = string.Empty;
    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; init; }
    [JsonPropertyName("kdf")] public KdfParameters Kdf { get; init; } = new();
    [JsonPropertyName("cipher")] public CipherParameters Cipher { get; init; } = new();
    /// <summary>
    /// Cantidad de direcciones de cobro adicionales ya entregadas (derivadas de la misma semilla).
    /// No es secreto ni se autentica: sin la semilla no permite calcular ninguna dirección, y si se
    /// altera solo cambia desde qué número se ofrecen direcciones nuevas (además se reconstruye
    /// buscando en la cadena las direcciones con movimientos).
    /// </summary>
    [JsonPropertyName("receiveIndex")] public int ReceiveIndex { get; init; }

    public const int MaxReceiveIndex = 1_000_000;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly AeadAlgorithm Aead = AeadAlgorithm.ChaCha20Poly1305;

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static WalletFile FromJson(string json)
    {
        WalletFile? file;
        try
        {
            file = JsonSerializer.Deserialize<WalletFile>(json);
        }
        catch (JsonException)
        {
            throw new WalletFormatException("El archivo no es un respaldo SHENT válido.");
        }
        if (file is null) throw new WalletFormatException("El archivo está vacío.");
        file.Validate();
        return file;
    }

    public void Validate()
    {
        if (Format != FormatId) throw new WalletFormatException("El archivo no es un respaldo SHENT.");
        if (Version != CurrentVersion) throw new WalletFormatException($"Versión de respaldo no soportada: {Version}.");
        if (Kdf.Name != "argon2id" || Kdf.Version != 19) throw new WalletFormatException("Algoritmo de derivación no soportado.");
        if (Kdf.MemoryKiB is < MinMemoryKiB or > MaxMemoryKiB || Kdf.Iterations is < 1 or > MaxIterations || Kdf.Parallelism != 1)
            throw new WalletFormatException("Parámetros de derivación fuera de los límites seguros.");
        if (Cipher.Name != "chacha20-poly1305") throw new WalletFormatException("Algoritmo de cifrado no soportado.");
        if (!TryBase64(Kdf.Salt, 16) || !TryBase64(Cipher.Nonce, 12) || !TryBase64(Cipher.Ciphertext, Ed25519Keys.SeedSize) || !TryBase64(Cipher.Tag, 16))
            throw new WalletFormatException("Datos cifrados con longitud inválida.");
        if (string.IsNullOrWhiteSpace(Network) || string.IsNullOrWhiteSpace(Address))
            throw new WalletFormatException("Faltan la red o la dirección.");
        if (ReceiveIndex is < 0 or > MaxReceiveIndex)
            throw new WalletFormatException("Índice de direcciones de cobro fuera de rango.");
    }

    private static bool TryBase64(string value, int expectedLength)
    {
        try
        {
            return Convert.FromBase64String(value).Length == expectedLength;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private byte[] AssociatedData()
    {
        var header = new
        {
            format = Format,
            version = Version,
            network = Network,
            address = Address,
            pseudonym = Pseudonym,
            kdf = new { Kdf.Name, Kdf.Version, Kdf.MemoryKiB, Kdf.Iterations, Kdf.Parallelism, Kdf.Salt },
            cipher = Cipher.Name,
        };
        return JsonSerializer.SerializeToUtf8Bytes(header);
    }

    private static byte[] DeriveKey(ReadOnlySpan<byte> password, KdfParameters kdf)
    {
        var algorithm = PasswordBasedKeyDerivationAlgorithm.Argon2id(new Argon2Parameters
        {
            DegreeOfParallelism = kdf.Parallelism,
            MemorySize = kdf.MemoryKiB,
            NumberOfPasses = kdf.Iterations,
        });
        var key = new byte[32];
        algorithm.DeriveBytes(password, Convert.FromBase64String(kdf.Salt), key);
        return key;
    }

    public static WalletFile Encrypt(ReadOnlySpan<byte> seed, string password, NetworkParams network, string label, KdfParameters? kdfTemplate = null)
    {
        if (seed.Length != Ed25519Keys.SeedSize) throw new ArgumentException("Semilla inválida.", nameof(seed));
        PasswordPolicy.EnsureAcceptable(password);

        using var key = Ed25519Keys.ImportSeed(seed);
        var address = Consensus.Address.FromPublicKey(Ed25519Keys.PublicKeyOf(key));
        var encoded = address.Encode(network);

        var kdf = (kdfTemplate ?? new KdfParameters()) with
        {
            Salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)),
        };
        var nonce = RandomNumberGenerator.GetBytes(12);
        var file = new WalletFile
        {
            Network = network.Name,
            Address = encoded,
            Pseudonym = Wallet.Pseudonym.FromAddress(address),
            Label = label,
            CreatedAt = DateTimeOffset.UtcNow,
            Kdf = kdf,
            Cipher = new CipherParameters { Nonce = Convert.ToBase64String(nonce) },
        };

        var passwordBytes = System.Text.Encoding.UTF8.GetBytes(password.Normalize(System.Text.NormalizationForm.FormC));
        var keyBytes = DeriveKey(passwordBytes, kdf);
        try
        {
            using var aeadKey = Key.Import(Aead, keyBytes, KeyBlobFormat.RawSymmetricKey);
            var sealedBytes = Aead.Encrypt(aeadKey, nonce, file.AssociatedData(), seed);
            var ciphertext = sealedBytes.AsSpan(0, sealedBytes.Length - Aead.TagSize).ToArray();
            var tag = sealedBytes.AsSpan(sealedBytes.Length - Aead.TagSize).ToArray();
            return file with
            {
                Cipher = file.Cipher with
                {
                    Ciphertext = Convert.ToBase64String(ciphertext),
                    Tag = Convert.ToBase64String(tag),
                },
            };
        }
        finally
        {
            Array.Clear(keyBytes);
            Array.Clear(passwordBytes);
        }
    }

    /// <summary>
    /// Descifra la semilla, recalcula la dirección pública y comprueba que coincide con la declarada.
    /// El llamador debe borrar la semilla devuelta cuando termine de usarla.
    /// </summary>
    public byte[] Decrypt(string password, NetworkParams expectedNetwork)
    {
        Validate();
        if (!string.Equals(Network, expectedNetwork.Name, StringComparison.Ordinal))
            throw new WalletFormatException($"Este respaldo es de la red '{Network}' y el nodo usa '{expectedNetwork.Name}'.");
        if (!Consensus.Address.TryParse(Address, expectedNetwork.AddressHrp, out var declared, out var error))
            throw new WalletFormatException($"Dirección declarada inválida: {error}");

        var passwordBytes = System.Text.Encoding.UTF8.GetBytes((password ?? string.Empty).Normalize(System.Text.NormalizationForm.FormC));
        var keyBytes = DeriveKey(passwordBytes, Kdf);
        try
        {
            using var aeadKey = Key.Import(Aead, keyBytes, KeyBlobFormat.RawSymmetricKey);
            var ciphertext = Convert.FromBase64String(Cipher.Ciphertext);
            var tag = Convert.FromBase64String(Cipher.Tag);
            var sealedBytes = new byte[ciphertext.Length + tag.Length];
            ciphertext.CopyTo(sealedBytes, 0);
            tag.CopyTo(sealedBytes, ciphertext.Length);
            var seed = new byte[ciphertext.Length];
            if (!Aead.Decrypt(aeadKey, Convert.FromBase64String(Cipher.Nonce), AssociatedData(), sealedBytes, seed))
            {
                Array.Clear(seed);
                throw new WalletPasswordException();
            }

            using var signingKey = Ed25519Keys.ImportSeed(seed);
            var derived = Consensus.Address.FromPublicKey(Ed25519Keys.PublicKeyOf(signingKey));
            if (!derived.Equals(declared))
            {
                Array.Clear(seed);
                throw new WalletFormatException("La clave descifrada no corresponde a la dirección declarada.");
            }
            return seed;
        }
        finally
        {
            Array.Clear(keyBytes);
            Array.Clear(passwordBytes);
        }
    }
}
