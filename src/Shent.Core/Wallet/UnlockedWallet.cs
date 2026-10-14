using System.Buffers.Binary;
using System.Security.Cryptography;
using NSec.Cryptography;
using Shent.Core.Consensus;
using Shent.Core.Crypto;
using Shent.Core.Model;

namespace Shent.Core.Wallet;

/// <summary>Una dirección de la billetera: la principal (índice 0) o una de cobro derivada de la semilla.</summary>
public sealed record WalletAddress(int Index, Address Address, string Encoded)
{
    public bool IsPrimary => Index == 0;
}

/// <summary>
/// Billetera desbloqueada en memoria. La clave privada vive en un objeto NSec (memoria protegida por
/// libsodium) y se destruye al bloquear la billetera o cerrar la aplicación. Nunca se escribe en disco
/// sin cifrar ni se registra en logs.
///
/// Además de la dirección principal, la billetera entrega una dirección nueva en cada cobro, derivada de
/// la misma semilla con HKDF-SHA256 (dominio "SHENT-SUBADDRESS-V1", red e índice). En la cadena esas
/// direcciones no tienen ninguna relación visible entre sí, y el mismo respaldo .shent alcanza para
/// recuperarlas todas: se regeneran desde la semilla y se buscan en la cadena.
/// </summary>
public sealed class UnlockedWallet : IDisposable
{
    public const int DiscoveryGap = 20;
    private static readonly byte[] SubaddressSalt = "SHENT-SUBADDRESS-V1"u8.ToArray();

    private readonly object _gate = new();
    private readonly NetworkParams _network;
    private readonly List<(WalletAddress Info, Key Key)> _keys = new();
    private bool _locked;

    public WalletFile File { get; private set; }
    public string FilePath { get; }
    public Address Address { get; }
    public string EncodedAddress { get; }
    public string Pseudonym { get; }
    /// <summary>Índice de la última dirección de cobro entregada (0 = solo la principal).</summary>
    public int ReceiveIndex { get; private set; }

    internal UnlockedWallet(WalletFile file, string filePath, byte[] seed, NetworkParams network)
    {
        File = file;
        FilePath = filePath;
        _network = network;
        var key = Ed25519Keys.ImportSeed(seed);
        Array.Clear(seed);
        Address = Address.FromPublicKey(Ed25519Keys.PublicKeyOf(key));
        EncodedAddress = Address.Encode(network);
        Pseudonym = Wallet.Pseudonym.FromAddress(Address);
        _keys.Add((new WalletAddress(0, Address, EncodedAddress), key));
        ReceiveIndex = file.ReceiveIndex;
        EnsureDerivedLocked(ReceiveIndex);
    }

    public bool IsLocked
    {
        get { lock (_gate) return _locked; }
    }

    /// <summary>Todas las direcciones conocidas, empezando por la principal.</summary>
    public IReadOnlyList<WalletAddress> Addresses
    {
        get { lock (_gate) return _keys.Select(k => k.Info).ToList(); }
    }

    public bool IsMine(Address address)
    {
        lock (_gate) return _keys.Any(k => k.Info.Address.Equals(address));
    }

    public WalletAddress? Find(Address address)
    {
        lock (_gate) return _keys.Select(k => k.Info).FirstOrDefault(a => a.Address.Equals(address));
    }

    /// <summary>Semilla de la dirección de cobro <paramref name="index"/> (≥ 1). El llamador debe borrarla.</summary>
    public static byte[] DeriveSubaddressSeed(ReadOnlySpan<byte> masterSeed, uint networkMagic, int index)
    {
        if (index < 1) throw new ArgumentOutOfRangeException(nameof(index));
        Span<byte> info = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(info, networkMagic);
        BinaryPrimitives.WriteUInt32BigEndian(info[4..], (uint)index);
        var output = new byte[Ed25519Keys.SeedSize];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, masterSeed, output, SubaddressSalt, info);
        return output;
    }

    private Key Primary => _locked ? throw new InvalidOperationException("La billetera está bloqueada.") : _keys[0].Key;

    private void EnsureDerivedLocked(int upTo)
    {
        if (_keys.Count > upTo) return;
        var master = Ed25519Keys.ExportSeed(Primary);
        try
        {
            for (var i = _keys.Count; i <= upTo; i++)
            {
                var childSeed = DeriveSubaddressSeed(master, _network.Magic, i);
                try
                {
                    var key = Ed25519Keys.ImportSeed(childSeed);
                    var address = Address.FromPublicKey(Ed25519Keys.PublicKeyOf(key));
                    _keys.Add((new WalletAddress(i, address, address.Encode(_network)), key));
                }
                finally
                {
                    Array.Clear(childSeed);
                }
            }
        }
        finally
        {
            Array.Clear(master);
        }
    }

    /// <summary>Entrega una dirección de cobro que nunca se mostró antes. Hay que guardar el archivo después.</summary>
    internal WalletAddress NextReceiveAddress()
    {
        lock (_gate)
        {
            if (ReceiveIndex >= WalletFile.MaxReceiveIndex) throw new InvalidOperationException("Se alcanzó el máximo de direcciones de cobro.");
            ReceiveIndex++;
            EnsureDerivedLocked(ReceiveIndex);
            return _keys[ReceiveIndex].Info;
        }
    }

    /// <summary>
    /// Busca en la cadena direcciones de cobro usadas más allá de las conocidas (p. ej. al restaurar un respaldo
    /// viejo en otra PC): revisa de a <see cref="DiscoveryGap"/> direcciones sin uso seguidas.
    /// Devuelve verdadero si encontró nuevas y avanzó <see cref="ReceiveIndex"/>.
    /// </summary>
    internal bool DiscoverUsed(Func<Address, bool> isUsed)
    {
        lock (_gate)
        {
            var advanced = false;
            var probe = ReceiveIndex + 1;
            var lastChecked = ReceiveIndex + DiscoveryGap;
            while (probe <= lastChecked)
            {
                EnsureDerivedLocked(probe);
                if (isUsed(_keys[probe].Info.Address))
                {
                    ReceiveIndex = probe;
                    lastChecked = probe + DiscoveryGap;
                    advanced = true;
                }
                probe++;
            }
            // Las direcciones derivadas para la búsqueda que no se usaron no se consideran entregadas.
            for (var i = _keys.Count - 1; i > ReceiveIndex; i--)
            {
                _keys[i].Key.Dispose();
                _keys.RemoveAt(i);
            }
            return advanced;
        }
    }

    private Key KeyFor(Address from)
    {
        lock (_gate)
        {
            if (_locked) throw new InvalidOperationException("La billetera está bloqueada.");
            foreach (var (info, key) in _keys)
            {
                if (info.Address.Equals(from)) return key;
            }
        }
        throw new InvalidOperationException("Esa dirección no pertenece a esta billetera.");
    }

    public Transaction CreateTransfer(NetworkParams network, Address recipient, long amount, long fee, long nonce, long expiresAtHeight, string memo = "") =>
        CreateTransfer(network, Address, recipient, amount, fee, nonce, expiresAtHeight, memo);

    /// <summary>Firma una transferencia desde una de las direcciones de la billetera.</summary>
    public Transaction CreateTransfer(NetworkParams network, Address from, Address recipient, long amount, long fee, long nonce, long expiresAtHeight, string memo = "")
    {
        var memoBytes = System.Text.Encoding.UTF8.GetBytes(memo ?? string.Empty);
        if (memoBytes.Length > Transaction.MaxMemoBytes) throw new ArgumentException($"El memo admite hasta {Transaction.MaxMemoBytes} bytes.");
        return Transaction.CreateTransfer(KeyFor(from), network.Magic, recipient, amount, fee, nonce, expiresAtHeight, memoBytes);
    }

    /// <summary>Genera un nuevo respaldo cifrado (opcionalmente con otra contraseña maestra).</summary>
    public WalletFile ReEncrypt(string newPassword, NetworkParams network)
    {
        byte[] seed;
        lock (_gate) seed = Ed25519Keys.ExportSeed(Primary);
        try
        {
            var file = WalletFile.Encrypt(seed, newPassword, network, File.Label);
            return file with { ReceiveIndex = ReceiveIndex };
        }
        finally
        {
            Array.Clear(seed);
        }
    }

    internal void ReplaceFile(WalletFile file) => File = file;

    public void Lock()
    {
        lock (_gate)
        {
            if (_locked) return;
            foreach (var (_, key) in _keys) key.Dispose();
            _keys.RemoveRange(1, _keys.Count - 1);
            _locked = true;
        }
    }

    public void Dispose() => Lock();
}
