using System.Security.Cryptography;
using NSec.Cryptography;
using Shent.Core.Chain;
using Shent.Core.Consensus;
using Shent.Core.Crypto;
using Shent.Core.Model;

namespace Shent.Core.Tests;

/// <summary>Cadena regtest en un directorio temporal con utilidades para minar bloques a mano.</summary>
public sealed class TestChain : IDisposable
{
    public string Directory { get; }
    public NetworkParams Network { get; }
    public Blockchain Chain { get; private set; }
    public long Now { get; set; }

    public TestChain(NetworkParams? network = null)
    {
        Network = network ?? NetworkParams.Regtest;
        Directory = Path.Combine(Path.GetTempPath(), "shent-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        Now = Network.GenesisTimestamp + 1_000_000;
        Chain = Open();
    }

    private Blockchain Open() => new(Network, Path.Combine(Directory, "chain.db"), () => Now);

    public void Reopen()
    {
        Chain.Dispose();
        Chain = Open();
    }

    public static Key NewKey() => Ed25519Keys.ImportSeed(Ed25519Keys.GenerateSeed());

    public static Address AddressOf(Key key) => Address.FromPublicKey(Ed25519Keys.PublicKeyOf(key));

    public Block BuildBlock(
        BlockIndex parent,
        Address payout,
        IList<Transaction>? transactions = null,
        long? coinbaseAmount = null,
        uint? bits = null,
        long? timestamp = null,
        long? coinbaseHeight = null)
    {
        var txs = transactions ?? [];
        var height = parent.Height + 1;
        var fees = txs.Sum(t => t.Fee);
        var amount = coinbaseAmount ?? Emission.SubsidyAt(Network, height) + fees;
        var coinbase = Transaction.CreateCoinbase(Network.Magic, coinbaseHeight ?? height, payout, amount, RandomNumberGenerator.GetBytes(8));
        var all = new List<Transaction> { coinbase };
        all.AddRange(txs);

        var header = new BlockHeader
        {
            NetworkMagic = Network.Magic,
            Height = height,
            PrevHash = parent.Hash,
            MerkleRoot = Block.ComputeMerkleRoot(all),
            Timestamp = timestamp ?? parent.Timestamp + 60,
            Bits = bits ?? Network.InitialBits,
        };
        return new Block { Header = Solve(header), Transactions = all };
    }

    public BlockHeader Solve(BlockHeader header)
    {
        var pow = Network.CreatePowHasher();
        var target = Target.FromCompact(header.Bits);
        var bytes = header.Serialize();
        for (ulong nonce = 0; ; nonce++)
        {
            BlockHeader.WriteNonce(bytes, nonce);
            if (PowHasher.MeetsTarget(pow.Hash(bytes), target))
            {
                return header.WithNonce(nonce);
            }
        }
    }

    public BlockAcceptResult MineOn(Address payout, IList<Transaction>? txs = null)
    {
        var block = BuildBlock(Chain.Tip, payout, txs);
        return Chain.ProcessBlock(block);
    }

    public void MineMany(Address payout, int count)
    {
        for (var i = 0; i < count; i++)
        {
            Assert.Equal(BlockAcceptStatus.NewTip, MineOn(payout).Status);
        }
    }

    public void Dispose()
    {
        Chain.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { System.IO.Directory.Delete(Directory, recursive: true); } catch (IOException) { }
    }
}
