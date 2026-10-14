using System.Text.Json;
using NSec.Cryptography;
using Shent.Core.Crypto;

namespace Shent.Core.P2P;

/// <summary>
/// Identificador estable de un nodo: la clave pública Ed25519 de su identidad, codificada en
/// Bech32m con el prefijo "peer" (ejemplo: peer1q…). Incluye checksum, así que un error de tipeo
/// se detecta al pegarlo.
/// </summary>
public sealed class PeerId : IEquatable<PeerId>, IComparable<PeerId>
{
    public const string Hrp = "peer";
    public const int Size = Ed25519Keys.PublicKeySize;

    private readonly byte[] _key;
    private string? _text;

    public PeerId(ReadOnlySpan<byte> publicKey)
    {
        if (publicKey.Length != Size) throw new ArgumentException("El Peer ID requiere una clave pública de 32 bytes.");
        _key = publicKey.ToArray();
    }

    public ReadOnlySpan<byte> PublicKey => _key;

    public override string ToString() => _text ??= Bech32m.Encode(Hrp, Bech32m.ConvertBits(_key, 8, 5, true)!);

    public string Short
    {
        get
        {
            var s = ToString();
            return $"{s[..11]}…{s[^6..]}";
        }
    }

    public static bool TryParse(string? text, out PeerId? id)
    {
        id = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (!Bech32m.TryDecode(text.Trim(), out var hrp, out var data5) || hrp != Hrp) return false;
        var bytes = Bech32m.ConvertBits(data5, 5, 8, false);
        if (bytes is null || bytes.Length != Size) return false;
        id = new PeerId(bytes);
        return true;
    }

    public bool Equals(PeerId? other) => other is not null && _key.AsSpan().SequenceEqual(other._key);
    public override bool Equals(object? obj) => obj is PeerId other && Equals(other);
    public override int GetHashCode() => BitConverter.ToInt32(_key, 0);
    public int CompareTo(PeerId? other) => other is null ? 1 : _key.AsSpan().SequenceCompareTo(other._key);
    public static bool operator ==(PeerId? a, PeerId? b) => a is null ? b is null : a.Equals(b);
    public static bool operator !=(PeerId? a, PeerId? b) => !(a == b);
}

/// <summary>
/// Identidad criptográfica persistente del nodo (Ed25519). Se guarda en &lt;datos&gt;/&lt;red&gt;/node_key.json
/// para que el Peer ID no cambie entre reinicios.
///
/// Esta clave NO controla monedas: solo sirve para que los demás nodos comprueben que hablan con el
/// mismo nodo de siempre (firma del desafío del handshake). No es la clave de ninguna billetera.
/// Si alguien la copia podría hacerse pasar por este nodo en la red, pero no gastar fondos.
/// El archivo se crea con permisos de lectura solo para el usuario en Linux/macOS.
/// </summary>
public sealed class NodeIdentity : IDisposable
{
    private const string FormatId = "shent-node-key";
    private static readonly byte[] AuthDomain = "SHENT-P2P-AUTH-V1"u8.ToArray();

    private readonly Key _key;

    public PeerId PeerId { get; }

    private NodeIdentity(byte[] seed)
    {
        _key = Ed25519Keys.ImportSeed(seed);
        Array.Clear(seed);
        PeerId = new PeerId(Ed25519Keys.PublicKeyOf(_key));
    }

    public static NodeIdentity Ephemeral() => new(Ed25519Keys.GenerateSeed());

    private sealed record KeyFile(string Format, int Version, string Seed);

    /// <summary>Carga la identidad o crea una nueva. Un archivo dañado se conserva como .corrupt y se genera otro.</summary>
    public static NodeIdentity LoadOrCreate(string path, Action<string>? warn = null)
    {
        if (File.Exists(path))
        {
            try
            {
                var file = JsonSerializer.Deserialize<KeyFile>(File.ReadAllText(path));
                if (file is { Format: FormatId, Version: 1 })
                {
                    var seed = Convert.FromHexString(file.Seed);
                    if (seed.Length == Ed25519Keys.SeedSize) return new NodeIdentity(seed);
                }
            }
            catch (Exception ex) when (ex is JsonException or FormatException or IOException)
            {
            }
            var backup = path + ".corrupt";
            File.Move(path, backup, overwrite: true);
            warn?.Invoke($"La identidad del nodo estaba dañada (copiada a {Path.GetFileName(backup)}). Se generó un Peer ID nuevo.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var newSeed = Ed25519Keys.GenerateSeed();
        var json = JsonSerializer.Serialize(new KeyFile(FormatId, 1, Convert.ToHexString(newSeed).ToLowerInvariant()));
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        File.Move(tmp, path, overwrite: true);
        return new NodeIdentity(newSeed);
    }

    /// <summary>Mensaje firmado en el handshake: liga el desafío del otro nodo, ambas identidades y la red.</summary>
    public static byte[] AuthMessage(uint networkMagic, Hash256 challenge, PeerId signer, PeerId verifier)
    {
        var data = new byte[AuthDomain.Length + 4 + Hash256.Size + PeerId.Size * 2];
        var span = data.AsSpan();
        AuthDomain.CopyTo(span);
        span = span[AuthDomain.Length..];
        BitConverter.TryWriteBytes(span, networkMagic);
        challenge.Span.CopyTo(span[4..]);
        signer.PublicKey.CopyTo(span[(4 + Hash256.Size)..]);
        verifier.PublicKey.CopyTo(span[(4 + Hash256.Size + PeerId.Size)..]);
        return data;
    }

    public byte[] SignHandshake(uint networkMagic, Hash256 remoteChallenge, PeerId remote) =>
        Ed25519Keys.Sign(_key, AuthMessage(networkMagic, remoteChallenge, PeerId, remote));

    public static bool VerifyHandshake(uint networkMagic, Hash256 localChallenge, PeerId signer, PeerId local, ReadOnlySpan<byte> signature) =>
        Ed25519Keys.Verify(signer.PublicKey, AuthMessage(networkMagic, localChallenge, signer, local), signature);

    public void Dispose() => _key.Dispose();
}
