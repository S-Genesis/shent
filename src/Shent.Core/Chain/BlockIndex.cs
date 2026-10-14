using System.Numerics;
using Shent.Core.Consensus;
using Shent.Core.Crypto;
using Shent.Core.Model;

namespace Shent.Core.Chain;

/// <summary>Entrada en memoria del árbol de bloques conocidos (cadena principal y ramas laterales).</summary>
public sealed class BlockIndex
{
    public required Hash256 Hash { get; init; }
    public required long Height { get; init; }
    public required BlockIndex? Prev { get; init; }
    public required long Timestamp { get; init; }
    public required uint Bits { get; init; }
    public required BigInteger TotalWork { get; init; }
    public BlockStatus Status { get; internal set; }

    public BlockIndex? GetAncestor(long height)
    {
        if (height > Height || height < 0) return null;
        var current = this;
        while (current is not null && current.Height > height)
        {
            current = current.Prev;
        }
        return current;
    }
}

public enum BlockAcceptStatus
{
    NewTip,
    SideChain,
    AlreadyKnown,
    Orphan,
    Invalid,
}

public sealed record BlockAcceptResult(BlockAcceptStatus Status, string? Reason = null, bool Punishable = false)
{
    public bool Accepted => Status is BlockAcceptStatus.NewTip or BlockAcceptStatus.SideChain;
}

public sealed record ChainUpdate(BlockIndex Tip, IReadOnlyList<Block> Connected, IReadOnlyList<Block> Disconnected);

public sealed record BlockSummary(
    long Height, Hash256 Hash, long Timestamp, int TransactionCount, uint Bits, Address Miner, long Reward, long Fees, int SizeBytes);

public sealed record TxLocation(Transaction Transaction, long Height, Hash256 BlockHash, long Timestamp, int Position, long Confirmations);

public sealed record TemplateContext(BlockIndex Parent, uint Bits, long MedianTimePast, long IssuedSupply);

/// <summary>Violación de una regla de consenso. <see cref="Punishable"/> indica que el emisor actuó de mala fe.</summary>
public sealed class ConsensusException(string message, bool punishable = true) : Exception(message)
{
    public bool Punishable { get; } = punishable;
}
