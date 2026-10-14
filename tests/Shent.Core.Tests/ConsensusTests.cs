using Shent.Core.Chain;
using Shent.Core.Consensus;
using Shent.Core.Mempool;
using Shent.Core.Model;

namespace Shent.Core.Tests;

public class EmissionTests
{
    [Fact]
    public void MainnetSupplyNeverExceedsOneHundredMillion()
    {
        var network = NetworkParams.Mainnet;
        var total = Emission.CumulativeSupply(network, long.MaxValue / 2);
        Assert.True(total <= Money.MaxSupply);
        Assert.True(total > Money.MaxSupply * 0.9999m);
    }

    [Fact]
    public void SubsidyHalvesOnSchedule()
    {
        var n = NetworkParams.Mainnet;
        Assert.Equal(0, Emission.SubsidyAt(n, 0));
        Assert.Equal(50 * Money.Coin, Emission.SubsidyAt(n, 1));
        Assert.Equal(50 * Money.Coin, Emission.SubsidyAt(n, n.HalvingInterval));
        Assert.Equal(25 * Money.Coin, Emission.SubsidyAt(n, n.HalvingInterval + 1));
        Assert.Equal(0, Emission.SubsidyAt(n, n.HalvingInterval * 64 + 1));
    }

    [Fact]
    public void CumulativeSupplyMatchesSumOfSubsidies()
    {
        var n = NetworkParams.Regtest;
        long sum = 0;
        for (long h = 1; h <= 1000; h++) sum += Emission.SubsidyAt(n, h);
        Assert.Equal(sum, Emission.CumulativeSupply(n, 1000));
    }
}

public class DifficultyTests
{
    private static List<DifficultyCalculator.BlockTiming> Window(NetworkParams n, long spacing, uint bits)
    {
        var list = new List<DifficultyCalculator.BlockTiming>();
        for (var i = 0; i <= n.DifficultyWindow; i++) list.Add(new(1_000_000 + i * spacing, bits));
        return list;
    }

    [Fact]
    public void StableWhenBlocksArriveOnTarget()
    {
        var n = NetworkParams.Mainnet;
        var next = DifficultyCalculator.NextBits(n, 1000, Window(n, n.TargetSpacingSeconds, n.InitialBits));
        var ratio = (double)Target.FromCompact(next) / (double)Target.FromCompact(n.InitialBits);
        Assert.InRange(ratio, 0.97, 1.03);
    }

    [Fact]
    public void HarderWhenBlocksAreFastAndEasierWhenSlow()
    {
        var n = NetworkParams.Mainnet;
        var start = Target.FromCompact(n.InitialBits);
        var fast = Target.FromCompact(DifficultyCalculator.NextBits(n, 1000, Window(n, n.TargetSpacingSeconds / 6, n.InitialBits)));
        var slow = Target.FromCompact(DifficultyCalculator.NextBits(n, 1000, Window(n, n.TargetSpacingSeconds * 5, n.InitialBits)));
        Assert.True(fast < start);
        Assert.True(slow > start);
        Assert.True(slow <= n.PowLimit);
    }

    [Fact]
    public void TargetIsTenMinutesWithFiftyCoinsPerBlock()
    {
        var n = NetworkParams.Mainnet;
        Assert.Equal(600, n.TargetSpacingSeconds);
        Assert.Equal(50 * Money.Coin, Emission.SubsidyAt(n, 1));
        Assert.True(Emission.CumulativeSupply(n, long.MaxValue / 2) <= n.MaxSupply);
    }

    [Fact]
    public void RetargetingStartsWithAPartialWindow()
    {
        // Con pocos bloques la dificultad ya reacciona (ventana parcial) en lugar de esperar 60 bloques a 10 min.
        var n = NetworkParams.Mainnet;
        var window = new List<DifficultyCalculator.BlockTiming>();
        for (var i = 0; i <= 10; i++) window.Add(new(1_000_000 + i * 30L, n.InitialBits));
        var next = Target.FromCompact(DifficultyCalculator.NextBits(n, 10, window));
        Assert.True(next < Target.FromCompact(n.InitialBits));
        Assert.Equal(n.InitialBits, DifficultyCalculator.NextBits(n, 2, window.Take(3).ToList()));
    }
}

public sealed class BlockchainTests : IDisposable
{
    private readonly TestChain _t = new();

    [Fact]
    public void GenesisIsDeterministic()
    {
        Assert.Equal(NetworkParams.Mainnet.CreateGenesisBlock().Hash, NetworkParams.Mainnet.CreateGenesisBlock().Hash);
        Assert.NotEqual(NetworkParams.Mainnet.CreateGenesisBlock().Hash, NetworkParams.Testnet.CreateGenesisBlock().Hash);
        Assert.Equal(0, _t.Chain.Height);
        Assert.Equal(_t.Chain.GenesisHash, _t.Chain.Tip.Hash);
    }

    [Fact]
    public void MiningCreditsRewardAndLedgerReconciles()
    {
        using var miner = TestChain.NewKey();
        var address = TestChain.AddressOf(miner);
        _t.MineMany(address, 3);

        Assert.Equal(3, _t.Chain.Height);
        Assert.Equal(3 * Emission.SubsidyAt(_t.Network, 1), _t.Chain.GetAccount(address).Balance);
        Assert.True(_t.Chain.Reconcile().Consistent);
        Assert.Equal(3, _t.Chain.GetAddressHistory(address).Count);
    }

    [Fact]
    public void ValidTransferMovesFundsAndFeesGoToMiner()
    {
        using var alice = TestChain.NewKey();
        using var bob = TestChain.NewKey();
        using var miner = TestChain.NewKey();
        var a = TestChain.AddressOf(alice);
        var b = TestChain.AddressOf(bob);
        var m = TestChain.AddressOf(miner);
        _t.MineMany(a, 2);

        var tx = Transaction.CreateTransfer(alice, _t.Network.Magic, b, 30 * Money.Coin, 5000, nonce: 0);
        Assert.Equal(BlockAcceptStatus.NewTip, _t.MineOn(m, [tx]).Status);

        var subsidy = Emission.SubsidyAt(_t.Network, 1);
        Assert.Equal(2 * subsidy - 30 * Money.Coin - 5000, _t.Chain.GetAccount(a).Balance);
        Assert.Equal(1, _t.Chain.GetAccount(a).Nonce);
        Assert.Equal(30 * Money.Coin, _t.Chain.GetAccount(b).Balance);
        Assert.Equal(subsidy + 5000, _t.Chain.GetAccount(m).Balance);
        Assert.True(_t.Chain.Reconcile().Consistent);

        var located = _t.Chain.GetTransaction(tx.Id);
        Assert.NotNull(located);
        Assert.Equal(3, located!.Height);
    }

    [Fact]
    public void DoubleSpendInsideOneBlockIsRejected()
    {
        using var alice = TestChain.NewKey();
        using var bob = TestChain.NewKey();
        using var carol = TestChain.NewKey();
        var a = TestChain.AddressOf(alice);
        _t.MineMany(a, 1);
        var tip = _t.Chain.Tip;

        var spend1 = Transaction.CreateTransfer(alice, _t.Network.Magic, TestChain.AddressOf(bob), 40 * Money.Coin, 1000, 0);
        var spend2 = Transaction.CreateTransfer(alice, _t.Network.Magic, TestChain.AddressOf(carol), 40 * Money.Coin, 1000, 0);
        var result = _t.Chain.ProcessBlock(_t.BuildBlock(tip, a, [spend1, spend2]));

        Assert.Equal(BlockAcceptStatus.Invalid, result.Status);
        Assert.Equal(tip.Hash, _t.Chain.Tip.Hash);
        Assert.Equal(Emission.SubsidyAt(_t.Network, 1), _t.Chain.GetAccount(a).Balance);
        Assert.True(_t.Chain.Reconcile().Consistent);
    }

    [Fact]
    public void SpendingNonexistentCoinsIsRejected()
    {
        using var alice = TestChain.NewKey();
        using var bob = TestChain.NewKey();
        var a = TestChain.AddressOf(alice);
        _t.MineMany(a, 1);
        var tx = Transaction.CreateTransfer(alice, _t.Network.Magic, TestChain.AddressOf(bob), 51 * Money.Coin, 1000, 0);
        var result = _t.Chain.ProcessBlock(_t.BuildBlock(_t.Chain.Tip, a, [tx]));
        Assert.Equal(BlockAcceptStatus.Invalid, result.Status);
        Assert.Contains("Saldo insuficiente", result.Reason);
    }

    [Fact]
    public void ReplayedTransactionIsRejectedByNonce()
    {
        using var alice = TestChain.NewKey();
        using var bob = TestChain.NewKey();
        var a = TestChain.AddressOf(alice);
        _t.MineMany(a, 2);
        var tx = Transaction.CreateTransfer(alice, _t.Network.Magic, TestChain.AddressOf(bob), Money.Coin, 1000, 0);
        Assert.Equal(BlockAcceptStatus.NewTip, _t.MineOn(a, [tx]).Status);

        var replay = _t.Chain.ProcessBlock(_t.BuildBlock(_t.Chain.Tip, a, [tx]));
        Assert.Equal(BlockAcceptStatus.Invalid, replay.Status);
        Assert.Contains("Nonce", replay.Reason);
    }

    [Fact]
    public void CoinbaseCannotClaimMoreThanSubsidyPlusFees()
    {
        using var miner = TestChain.NewKey();
        var block = _t.BuildBlock(_t.Chain.Tip, TestChain.AddressOf(miner), coinbaseAmount: Emission.SubsidyAt(_t.Network, 1) + 1);
        var result = _t.Chain.ProcessBlock(block);
        Assert.Equal(BlockAcceptStatus.Invalid, result.Status);
        Assert.Equal(0, _t.Chain.IssuedSupply);
    }

    [Fact]
    public void WrongDifficultyAndWrongHeightAreRejected()
    {
        using var miner = TestChain.NewKey();
        var m = TestChain.AddressOf(miner);
        var otherBits = Target.ToCompact(_t.Network.PowLimit - 1);
        Assert.NotEqual(_t.Network.InitialBits, otherBits);
        Assert.Equal(BlockAcceptStatus.Invalid, _t.Chain.ProcessBlock(_t.BuildBlock(_t.Chain.Tip, m, bits: otherBits)).Status);
        Assert.Equal(BlockAcceptStatus.Invalid, _t.Chain.ProcessBlock(_t.BuildBlock(_t.Chain.Tip, m, coinbaseHeight: 7)).Status);
    }

    [Fact]
    public void InsufficientProofOfWorkIsRejected()
    {
        using var miner = TestChain.NewKey();
        var block = _t.BuildBlock(_t.Chain.Tip, TestChain.AddressOf(miner));
        var pow = _t.Network.CreatePowHasher();
        var target = Target.FromCompact(block.Header.Bits);
        for (ulong nonce = 0; ; nonce++)
        {
            var header = block.Header.WithNonce(nonce);
            if (!Crypto.PowHasher.MeetsTarget(pow.Hash(header.Serialize()), target))
            {
                var bad = new Block { Header = header, Transactions = block.Transactions };
                var result = _t.Chain.ProcessBlock(bad);
                Assert.Equal(BlockAcceptStatus.Invalid, result.Status);
                Assert.Contains("Prueba de trabajo", result.Reason);
                return;
            }
        }
    }

    [Fact]
    public void FutureTimestampIsRejectedWithoutPunishment()
    {
        using var miner = TestChain.NewKey();
        var block = _t.BuildBlock(_t.Chain.Tip, TestChain.AddressOf(miner), timestamp: _t.Now + _t.Network.MaxFutureDriftSeconds + 10);
        var result = _t.Chain.ProcessBlock(block);
        Assert.Equal(BlockAcceptStatus.Invalid, result.Status);
        Assert.False(result.Punishable);

        _t.Now += 3600 * 3;
        Assert.Equal(BlockAcceptStatus.NewTip, _t.Chain.ProcessBlock(block).Status);
    }

    [Fact]
    public void ManipulatedMerkleRootIsRejected()
    {
        using var miner = TestChain.NewKey();
        using var thief = TestChain.NewKey();
        var block = _t.BuildBlock(_t.Chain.Tip, TestChain.AddressOf(miner));
        var stolen = Transaction.CreateCoinbase(_t.Network.Magic, 1, TestChain.AddressOf(thief), block.Coinbase.Amount, []);
        var forged = new Block { Header = block.Header, Transactions = [stolen] };
        Assert.Equal(BlockAcceptStatus.Invalid, _t.Chain.ProcessBlock(forged).Status);
    }

    [Fact]
    public void ReorganizesToChainWithMoreWorkAndRestoresBalances()
    {
        using var alice = TestChain.NewKey();
        using var bob = TestChain.NewKey();
        var a = TestChain.AddressOf(alice);
        var b = TestChain.AddressOf(bob);
        var genesis = _t.Chain.Tip;

        // Rama A: 2 bloques para Alice.
        var a1 = _t.BuildBlock(genesis, a);
        Assert.Equal(BlockAcceptStatus.NewTip, _t.Chain.ProcessBlock(a1).Status);
        var a1Index = _t.Chain.Tip;
        var a2 = _t.BuildBlock(a1Index, a);
        Assert.Equal(BlockAcceptStatus.NewTip, _t.Chain.ProcessBlock(a2).Status);
        Assert.Equal(2 * Emission.SubsidyAt(_t.Network, 1), _t.Chain.GetAccount(a).Balance);

        // Rama B: 3 bloques para Bob desde el génesis (más trabajo acumulado).
        var b1 = _t.BuildBlock(genesis, b, timestamp: genesis.Timestamp + 61);
        Assert.Equal(BlockAcceptStatus.SideChain, _t.Chain.ProcessBlock(b1).Status);
        var b2 = _t.BuildBlock(IndexOf(b1), b);
        Assert.Equal(BlockAcceptStatus.SideChain, _t.Chain.ProcessBlock(b2).Status);
        var b3 = _t.BuildBlock(IndexOf(b2), b);
        Assert.Equal(BlockAcceptStatus.NewTip, _t.Chain.ProcessBlock(b3).Status);

        Assert.Equal(3, _t.Chain.Height);
        Assert.Equal(b3.Hash, _t.Chain.Tip.Hash);
        Assert.Equal(0, _t.Chain.GetAccount(a).Balance);
        Assert.Equal(3 * Emission.SubsidyAt(_t.Network, 1), _t.Chain.GetAccount(b).Balance);
        Assert.Empty(_t.Chain.GetAddressHistory(a));
        Assert.True(_t.Chain.Reconcile().Consistent);
    }

    [Fact]
    public void InvalidBlockInHeavierBranchDoesNotCorruptState()
    {
        using var alice = TestChain.NewKey();
        using var bob = TestChain.NewKey();
        var a = TestChain.AddressOf(alice);
        var b = TestChain.AddressOf(bob);
        var genesis = _t.Chain.Tip;
        _t.MineMany(a, 1);
        var tipBefore = _t.Chain.Tip.Hash;

        var b1 = _t.BuildBlock(genesis, b, timestamp: genesis.Timestamp + 61);
        _t.Chain.ProcessBlock(b1);
        // b2 gasta monedas que Bob no tiene: la rama B no puede activarse.
        var badTx = Transaction.CreateTransfer(bob, _t.Network.Magic, a, 500 * Money.Coin, 1000, 0);
        var b2 = _t.BuildBlock(IndexOf(b1), b, [badTx]);
        var result = _t.Chain.ProcessBlock(b2);

        Assert.Equal(BlockAcceptStatus.Invalid, result.Status);
        Assert.Equal(tipBefore, _t.Chain.Tip.Hash);
        Assert.Equal(Emission.SubsidyAt(_t.Network, 1), _t.Chain.GetAccount(a).Balance);
        Assert.Equal(0, _t.Chain.GetAccount(b).Balance);
        Assert.True(_t.Chain.Reconcile().Consistent);

        // Los descendientes de un bloque inválido también se rechazan.
        var b3 = _t.BuildBlock(IndexOfStored(b2), b);
        Assert.Equal(BlockAcceptStatus.Invalid, _t.Chain.ProcessBlock(b3).Status);
    }

    [Fact]
    public void StatePersistsAcrossRestart()
    {
        using var alice = TestChain.NewKey();
        var a = TestChain.AddressOf(alice);
        _t.MineMany(a, 4);
        var tip = _t.Chain.Tip.Hash;
        _t.Reopen();
        Assert.Equal(tip, _t.Chain.Tip.Hash);
        Assert.Equal(4 * Emission.SubsidyAt(_t.Network, 1), _t.Chain.GetAccount(a).Balance);
        Assert.True(_t.Chain.Reconcile().Consistent);
    }

    [Fact]
    public void LocatorFindsCommonAncestor()
    {
        using var alice = TestChain.NewKey();
        _t.MineMany(TestChain.AddressOf(alice), 25);
        var locator = _t.Chain.GetLocator();
        Assert.Equal(_t.Chain.Tip.Hash, locator[0]);
        Assert.Equal(_t.Chain.GenesisHash, locator[^1]);

        var fromGenesis = _t.Chain.GetHashesAfterLocator([_t.Chain.GenesisHash], Crypto.Hash256.Zero, 500);
        Assert.Equal(25, fromGenesis.Count);
        var fromTip = _t.Chain.GetHashesAfterLocator(locator, Crypto.Hash256.Zero, 500);
        Assert.Empty(fromTip);
    }

    private BlockIndex IndexOf(Block block) => new()
    {
        Hash = block.Hash,
        Height = block.Header.Height,
        Prev = null,
        Timestamp = block.Header.Timestamp,
        Bits = block.Header.Bits,
        TotalWork = 0,
    };

    private BlockIndex IndexOfStored(Block block) => IndexOf(block);

    public void Dispose() => _t.Dispose();
}

public sealed class MempoolTests : IDisposable
{
    private readonly TestChain _t = new();
    private readonly TxMempool _pool;

    public MempoolTests()
    {
        _pool = new TxMempool(_t.Chain);
    }

    [Fact]
    public void AcceptsValidAndRejectsDoubleSpendWithSameNonce()
    {
        using var alice = TestChain.NewKey();
        using var bob = TestChain.NewKey();
        using var carol = TestChain.NewKey();
        _t.MineMany(TestChain.AddressOf(alice), 1);

        var first = Transaction.CreateTransfer(alice, _t.Network.Magic, TestChain.AddressOf(bob), 10 * Money.Coin, 2000, 0);
        var conflict = Transaction.CreateTransfer(alice, _t.Network.Magic, TestChain.AddressOf(carol), 10 * Money.Coin, 9000, 0);
        Assert.True(_pool.TryAdd(first).Accepted);
        var result = _pool.TryAdd(conflict);
        Assert.False(result.Accepted);
        Assert.Contains("doble gasto", result.Error);
        Assert.False(_pool.TryAdd(first).Accepted);
    }

    [Fact]
    public void EnforcesNonceSequenceBalanceAndMinimumFee()
    {
        using var alice = TestChain.NewKey();
        using var bob = TestChain.NewKey();
        var b = TestChain.AddressOf(bob);
        _t.MineMany(TestChain.AddressOf(alice), 1);

        Assert.False(_pool.TryAdd(Transaction.CreateTransfer(alice, _t.Network.Magic, b, Money.Coin, 2000, 5)).Accepted);
        Assert.False(_pool.TryAdd(Transaction.CreateTransfer(alice, _t.Network.Magic, b, Money.Coin, 1, 0)).Accepted);
        Assert.True(_pool.TryAdd(Transaction.CreateTransfer(alice, _t.Network.Magic, b, 30 * Money.Coin, 2000, 0)).Accepted);
        var over = _pool.TryAdd(Transaction.CreateTransfer(alice, _t.Network.Magic, b, 30 * Money.Coin, 2000, 1));
        Assert.False(over.Accepted);
        Assert.Contains("Saldo insuficiente", over.Error);
        Assert.Equal(1, _pool.NextNonce(TestChain.AddressOf(alice)));
    }

    [Fact]
    public void RejectsInvalidSignatureAsPunishable()
    {
        using var alice = TestChain.NewKey();
        using var bob = TestChain.NewKey();
        _t.MineMany(TestChain.AddressOf(alice), 1);
        var tx = Transaction.CreateTransfer(alice, _t.Network.Magic, TestChain.AddressOf(bob), Money.Coin, 2000, 0);
        tx.Signature = new byte[64];
        var result = _pool.TryAdd(tx);
        Assert.False(result.Accepted);
        Assert.True(result.Punishable);
    }

    [Fact]
    public void ConfirmedTransactionsLeaveThePool()
    {
        using var alice = TestChain.NewKey();
        using var bob = TestChain.NewKey();
        var a = TestChain.AddressOf(alice);
        _t.MineMany(a, 1);
        var tx = Transaction.CreateTransfer(alice, _t.Network.Magic, TestChain.AddressOf(bob), Money.Coin, 2000, 0);
        Assert.True(_pool.TryAdd(tx).Accepted);
        var selected = _pool.SelectForBlock(_t.Network.MaxBlockBytes);
        Assert.Single(selected);
        Assert.Equal(BlockAcceptStatus.NewTip, _t.MineOn(a, selected).Status);
        Assert.Equal(0, _pool.Count);
    }

    public void Dispose() => _t.Dispose();
}
