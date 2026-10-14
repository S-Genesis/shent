using System.Security.Cryptography;
using Shent.Core.Chain;
using Shent.Core.Consensus;
using Shent.Core.Mempool;
using Shent.Core.Model;

namespace Shent.Core.Mining;

public sealed record BlockTemplate(Block Block, byte[] HeaderBytes, System.Numerics.BigInteger Target, long Height, long Reward, DateTimeOffset CreatedAt);

public static class BlockTemplateBuilder
{
    private const int CoinbaseReserveBytes = 400;

    public static BlockTemplate Build(Blockchain chain, TxMempool mempool, Address payout, string tag, Func<long>? clock = null)
    {
        var network = chain.Network;
        var ctx = chain.GetTemplateContext();
        var height = ctx.Parent.Height + 1;
        var now = (clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds()))();
        var timestamp = Math.Max(now, ctx.MedianTimePast + 1);

        var selected = mempool.SelectForBlock(network.MaxBlockBytes - BlockHeader.Size - CoinbaseReserveBytes);
        var fees = selected.Sum(t => t.Fee);
        var subsidy = Emission.SubsidyAt(network, height);
        if (ctx.IssuedSupply + subsidy > network.MaxSupply) subsidy = 0;

        var extra = new byte[Math.Min(Transaction.MaxExtraBytes, 8 + System.Text.Encoding.UTF8.GetByteCount(tag))];
        RandomNumberGenerator.Fill(extra.AsSpan(0, 8));
        System.Text.Encoding.UTF8.GetBytes(tag).AsSpan(0, extra.Length - 8).CopyTo(extra.AsSpan(8));

        var coinbase = Transaction.CreateCoinbase(network.Magic, height, payout, subsidy + fees, extra);
        var txs = new List<Transaction>(selected.Count + 1) { coinbase };
        txs.AddRange(selected);

        var header = new BlockHeader
        {
            NetworkMagic = network.Magic,
            Height = height,
            PrevHash = ctx.Parent.Hash,
            MerkleRoot = Block.ComputeMerkleRoot(txs),
            Timestamp = timestamp,
            Bits = ctx.Bits,
            Nonce = 0,
        };
        var block = new Block { Header = header, Transactions = txs };
        return new BlockTemplate(block, header.Serialize(), Target.FromCompact(ctx.Bits), height, subsidy + fees, DateTimeOffset.UtcNow);
    }
}
