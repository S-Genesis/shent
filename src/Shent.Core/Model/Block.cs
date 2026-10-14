using Shent.Core.Crypto;
using Shent.Core.Encoding;

namespace Shent.Core.Model;

public sealed class Block
{
    public const int MaxTransactions = 4000;

    private Hash256? _hash;

    public required BlockHeader Header { get; init; }
    public required IReadOnlyList<Transaction> Transactions { get; init; }

    public Hash256 Hash => _hash ??= Header.ComputeHash();

    public Transaction Coinbase => Transactions[0];

    public byte[] Serialize()
    {
        var w = new ShentWriter(BlockHeader.Size + Transactions.Count * 200);
        w.WriteBytes(Header.Serialize());
        w.WriteVarInt((ulong)Transactions.Count);
        foreach (var tx in Transactions)
        {
            w.WriteVarBytes(tx.Serialize());
        }
        return w.ToArray();
    }

    public static Block Deserialize(ReadOnlyMemory<byte> data, int maxBytes)
    {
        if (data.Length > maxBytes)
        {
            throw new FormatException("Bloque demasiado grande.");
        }
        var r = new ShentReader(data);
        var header = BlockHeader.Read(r);
        var count = r.ReadCount(MaxTransactions);
        if (count == 0)
        {
            throw new FormatException("El bloque no contiene transacciones.");
        }
        var txs = new List<Transaction>(count);
        for (var i = 0; i < count; i++)
        {
            var raw = r.ReadVarBytes(Transaction.MaxSerializedSize);
            txs.Add(Transaction.Deserialize(raw));
        }
        r.EnsureEnd();
        return new Block { Header = header, Transactions = txs };
    }

    public static Hash256 ComputeMerkleRoot(IReadOnlyList<Transaction> transactions)
    {
        return MerkleTree.ComputeRoot(transactions.Select(t => t.Id).ToList());
    }
}

public static class MerkleTree
{
    /// <summary>Árbol Merkle con SHA-256d; si un nivel es impar se duplica el último elemento.</summary>
    public static Hash256 ComputeRoot(IReadOnlyList<Hash256> leaves)
    {
        if (leaves.Count == 0) return Hash256.Zero;
        var level = leaves.ToList();
        Span<byte> pair = stackalloc byte[Hash256.Size * 2];
        while (level.Count > 1)
        {
            var next = new List<Hash256>((level.Count + 1) / 2);
            for (var i = 0; i < level.Count; i += 2)
            {
                var left = level[i];
                var right = i + 1 < level.Count ? level[i + 1] : level[i];
                left.Span.CopyTo(pair);
                right.Span.CopyTo(pair[Hash256.Size..]);
                next.Add(Hash256.Sha256d(pair));
            }
            level = next;
        }
        return level[0];
    }
}
