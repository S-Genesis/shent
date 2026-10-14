using Shent.Core.Chain;
using Shent.Core.Consensus;
using Shent.Core.Model;

namespace Shent.Core.Tests;

public sealed class ChainStatisticsTests : IDisposable
{
    private readonly TestChain _t = new();

    private static BlockIndex IndexOf(Block block) => new()
    {
        Hash = block.Hash,
        Height = block.Header.Height,
        Prev = null,
        Timestamp = block.Header.Timestamp,
        Bits = block.Header.Bits,
        TotalWork = 0,
    };

    [Fact]
    public void CountsBlocksMinersAndSupplyFromTheValidatedChain()
    {
        using var alice = TestChain.NewKey();
        using var bob = TestChain.NewKey();
        var a = TestChain.AddressOf(alice);
        var b = TestChain.AddressOf(bob);
        _t.MineMany(a, 5);
        _t.MineMany(b, 3);
        var stats = new ChainStatistics(_t.Chain);
        var now = _t.Chain.Tip.Timestamp + 10;

        var s = stats.Snapshot(now);
        Assert.Equal(8, s.Height);
        Assert.Equal(Emission.CumulativeSupply(_t.Network, 8), s.Issued);
        Assert.Equal(_t.Chain.IssuedSupply, s.Issued);
        Assert.Equal(8, s.Blocks24h);
        Assert.Equal(2, s.Miners24h);
        Assert.Equal(2, s.Miners7d);
        Assert.Equal(2, s.MinersTotal);
        Assert.Equal(60, s.AverageBlockSeconds24h);
        Assert.True(s.Hashrate24h > 0);
        Assert.Equal(a, s.TopMiners7d[0].Miner);
        Assert.Equal(5, s.TopMiners7d[0].Blocks);
        Assert.Equal(5.0 / 8, s.TopMiners7d[0].Share, 6);
        Assert.Equal(8, s.Days.Sum(d => d.Blocks));
        Assert.Equal(s.Issued, s.Days[^1].IssuedAtEnd);

        // Un día después ya no cuentan como "últimas 24 h", pero siguen en el total.
        var later = stats.Snapshot(now + 86_400 + 100);
        Assert.Equal(0, later.Blocks24h);
        Assert.Equal(0, later.Miners24h);
        Assert.Equal(2, later.MinersTotal);
        Assert.Equal(2, later.Miners7d);
    }

    [Fact]
    public void UpdatesIncrementallyAndUndoesReorganizedBlocks()
    {
        using var alice = TestChain.NewKey();
        using var bob = TestChain.NewKey();
        var a = TestChain.AddressOf(alice);
        var b = TestChain.AddressOf(bob);
        var genesis = _t.Chain.Tip;
        _t.MineMany(a, 2);
        var stats = new ChainStatistics(_t.Chain);
        Assert.Equal(1, stats.Snapshot(_t.Chain.Tip.Timestamp).MinersTotal);

        // Rama de Bob con más trabajo desde el génesis: los bloques de Alice dejan de contar.
        var b1 = _t.BuildBlock(genesis, b, timestamp: genesis.Timestamp + 61);
        Assert.Equal(BlockAcceptStatus.SideChain, _t.Chain.ProcessBlock(b1).Status);
        var b2 = _t.BuildBlock(IndexOf(b1), b);
        Assert.Equal(BlockAcceptStatus.SideChain, _t.Chain.ProcessBlock(b2).Status);
        var b3 = _t.BuildBlock(IndexOf(b2), b);
        Assert.Equal(BlockAcceptStatus.NewTip, _t.Chain.ProcessBlock(b3).Status);

        var s = stats.Snapshot(_t.Chain.Tip.Timestamp);
        Assert.Equal(3, s.Height);
        Assert.Equal(1, s.MinersTotal);
        Assert.Equal(b, s.TopMiners7d.Single().Miner);
        Assert.Equal(3, s.Days.Sum(d => d.Blocks));
        Assert.Equal(_t.Chain.IssuedSupply, s.Issued);

        _t.MineMany(a, 1);
        var after = stats.Snapshot(_t.Chain.Tip.Timestamp);
        Assert.Equal(4, after.Days.Sum(d => d.Blocks));
        Assert.Equal(2, after.MinersTotal);
    }

    [Fact]
    public void EmptyChainHasNoMinersAndNoDivisionByZero()
    {
        var s = new ChainStatistics(_t.Chain).Snapshot(_t.Chain.Tip.Timestamp + 5);
        Assert.Equal(0, s.Height);
        Assert.Equal(0, s.MinersTotal);
        Assert.Equal(0, s.Blocks24h);
        Assert.Equal(0, s.Hashrate24h);
        Assert.Empty(s.TopMiners7d);
        Assert.NotEmpty(s.Days);
    }

    public void Dispose() => _t.Dispose();
}
