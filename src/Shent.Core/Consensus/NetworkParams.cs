using System.Numerics;
using Shent.Core.Crypto;
using Shent.Core.Model;

namespace Shent.Core.Consensus;

/// <summary>
/// Reglas de consenso por red. Cambiar cualquiera de estos valores en una red en funcionamiento
/// produce una bifurcación: todos los nodos deben actualizarse a la vez.
/// </summary>
public sealed record NetworkParams
{
    public required string Name { get; init; }
    public required uint Magic { get; init; }
    public required string AddressHrp { get; init; }
    public required int DefaultPort { get; init; }
    public int LanDiscoveryPort => DefaultPort + 2;

    public required BigInteger PowLimit { get; init; }
    public required BigInteger InitialTarget { get; init; }
    public uint InitialBits => Target.ToCompact(InitialTarget);
    /// <summary>Tiempo objetivo entre bloques: 10 minutos.</summary>
    public int TargetSpacingSeconds { get; init; } = 600;
    public int DifficultyWindow { get; init; } = 60;
    public bool NoRetargeting { get; init; }
    public required int PowMemoryKiB { get; init; }
    public int PowPasses { get; init; } = 1;

    public long InitialSubsidy { get; init; } = 50 * Money.Coin;
    public required long HalvingInterval { get; init; }
    public long MaxSupply { get; init; } = Money.MaxSupply;

    public int MaxFutureDriftSeconds { get; init; } = 300;
    public int MedianTimeSpan { get; init; } = 11;
    public int MaxBlockBytes { get; init; } = 1_000_000;
    public int MaxForkDepth { get; init; } = 288;
    public long MinRelayFee { get; init; } = 1_000;

    public required long GenesisTimestamp { get; init; }
    public string GenesisMessage { get; init; } = "SHENT 13/10/2026 - moneda comunitaria sin valor monetario";

    public IReadOnlyList<string> Seeds { get; init; } = [];
    public IReadOnlyDictionary<long, Hash256> Checkpoints { get; init; } = new Dictionary<long, Hash256>();
    public long LastCheckpointHeight => Checkpoints.Count == 0 ? 0 : Checkpoints.Keys.Max();

    public static readonly Address BurnAddress = Address.FromHash(new byte[Address.HashSize]);

    public PowHasher CreatePowHasher() => new(PowMemoryKiB, PowPasses);

    /// <summary>
    /// El génesis es idéntico en todos los nodos de una red y no requiere prueba de trabajo:
    /// se acepta por su hash, que se deriva de los parámetros de la red.
    /// </summary>
    public Block CreateGenesisBlock()
    {
        var coinbase = Transaction.CreateCoinbase(Magic, 0, BurnAddress, 0, System.Text.Encoding.UTF8.GetBytes(GenesisMessage));
        var txs = new[] { coinbase };
        var header = new BlockHeader
        {
            NetworkMagic = Magic,
            Height = 0,
            PrevHash = Hash256.Zero,
            MerkleRoot = Block.ComputeMerkleRoot(txs),
            Timestamp = GenesisTimestamp,
            Bits = InitialBits,
            Nonce = 0,
        };
        return new Block { Header = header, Transactions = txs };
    }

    // 2026-10-13T00:00:00Z
    private const long LaunchTimestamp = 1_791_849_600;

    /// <summary>
    /// Red principal. Puerto P2P 37373/TCP, descubrimiento LAN 37375/UDP.
    /// Bloques cada ~10 minutos con 50 SHENT y halving cada 1.000.000 de bloques (~19 años):
    /// es el intervalo que mantiene el suministro total por debajo de 100.000.000 SHENT.
    /// La dificultad inicial supone ~1.000 H/s en toda la red (un hilo de CPU con Argon2id de 2 MiB).
    /// </summary>
    public static readonly NetworkParams Mainnet = new()
    {
        Name = "mainnet",
        Magic = 0x53484E54, // "SHNT"
        AddressHrp = "shent",
        DefaultPort = 37373,
        PowMemoryKiB = 2048,
        PowLimit = Target.TwoPow256 / 2_000,
        InitialTarget = Target.TwoPow256 / 600_000,
        HalvingInterval = 1_000_000,
        GenesisTimestamp = LaunchTimestamp,
        Checkpoints = CheckpointData.For("mainnet"),
    };

    /// <summary>Red de pruebas pública. Puerto P2P 47373/TCP.</summary>
    public static readonly NetworkParams Testnet = new()
    {
        Name = "testnet",
        Magic = 0x54534854, // "TSHT"
        AddressHrp = "tshent",
        DefaultPort = 47373,
        PowMemoryKiB = 2048,
        PowLimit = Target.TwoPow256 / 500,
        InitialTarget = Target.TwoPow256 / 300_000,
        HalvingInterval = 1_000_000,
        GenesisTimestamp = LaunchTimestamp,
        GenesisMessage = "SHENT testnet - monedas de prueba sin valor",
        Checkpoints = CheckpointData.For("testnet"),
    };

    /// <summary>Red local para desarrollo y pruebas automáticas: dificultad trivial y sin reajuste.</summary>
    public static readonly NetworkParams Regtest = new()
    {
        Name = "regtest",
        Magic = 0x52534854, // "RSHT"
        AddressHrp = "rshent",
        DefaultPort = 57373,
        PowMemoryKiB = 8,
        PowLimit = Target.TwoPow256 / 2,
        InitialTarget = Target.TwoPow256 / 2,
        NoRetargeting = true,
        HalvingInterval = 150,
        MaxFutureDriftSeconds = 7200,
        MaxForkDepth = 10_000,
        GenesisTimestamp = LaunchTimestamp,
        GenesisMessage = "SHENT regtest",
    };

    public static NetworkParams FromName(string name) => name.Trim().ToLowerInvariant() switch
    {
        "mainnet" or "main" => Mainnet,
        "testnet" or "test" => Testnet,
        "regtest" => Regtest,
        _ => throw new ArgumentException($"Red desconocida: {name}"),
    };
}
