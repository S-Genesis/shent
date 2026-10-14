using System.Text.Json.Nodes;
using Shent.Core.Consensus;
using Shent.Core.Crypto;
using Shent.Core.Wallet;

namespace Shent.Core.Tests;

/// <summary>Direcciones de cobro derivadas de la semilla y pagos desde varias direcciones.</summary>
public sealed class SubaddressTests : IDisposable
{
    private static readonly KdfParameters FastKdf = new() { MemoryKiB = WalletFile.MinMemoryKiB, Iterations = 1 };
    private const string Password = "CLAVE-MAESTRA-DE-PRUEBA-1";
    private readonly List<string> _dirs = [];

    private string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "shent-subaddress-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        return dir;
    }

    [Fact]
    public void DerivationIsDeterministicDistinctAndBoundToTheNetwork()
    {
        var seed = Ed25519Keys.GenerateSeed();
        var a1 = UnlockedWallet.DeriveSubaddressSeed(seed, NetworkParams.Mainnet.Magic, 1);
        var a1Again = UnlockedWallet.DeriveSubaddressSeed(seed, NetworkParams.Mainnet.Magic, 1);
        var a2 = UnlockedWallet.DeriveSubaddressSeed(seed, NetworkParams.Mainnet.Magic, 2);
        var t1 = UnlockedWallet.DeriveSubaddressSeed(seed, NetworkParams.Testnet.Magic, 1);
        Assert.Equal(a1, a1Again);
        Assert.NotEqual(a1, a2);
        Assert.NotEqual(a1, t1);
        Assert.NotEqual(seed, a1);
        Assert.Throws<ArgumentOutOfRangeException>(() => UnlockedWallet.DeriveSubaddressSeed(seed, NetworkParams.Mainnet.Magic, 0));
    }

    [Fact]
    public void EachReceiveIsANewAddressAndTheBackupRecoversThemAll()
    {
        var device = new WalletManager(NetworkParams.Mainnet, TempDir(), FastKdf);
        using var wallet = device.Create(Password, "principal");
        var r1 = device.NewReceiveAddress(wallet);
        var r2 = device.NewReceiveAddress(wallet);
        Assert.Equal(1, r1.Index);
        Assert.Equal(2, r2.Index);
        Assert.NotEqual(wallet.Address, r1.Address);
        Assert.NotEqual(r1.Address, r2.Address);
        Assert.True(wallet.IsMine(r2.Address));
        Assert.Equal(3, wallet.Addresses.Count);

        // El avance queda guardado: al reabrir no se repiten direcciones.
        using (var reopened = device.Unlock(wallet.FilePath, Password))
        {
            Assert.Equal(2, reopened.ReceiveIndex);
            Assert.Equal(r2.Address, reopened.Addresses[2].Address);
            Assert.Equal(3, device.NewReceiveAddress(reopened).Index);
        }

        // Otra PC con el mismo respaldo obtiene las mismas direcciones.
        var json = File.ReadAllText(wallet.FilePath);
        var other = new WalletManager(NetworkParams.Mainnet, TempDir(), FastKdf);
        using var restored = other.Import(json, Password);
        Assert.Equal(r1.Address, restored.Addresses[1].Address);
        Assert.Equal(r2.Address, restored.Addresses[2].Address);
    }

    [Fact]
    public void OldBackupDiscoversUsedAddressesFromTheChain()
    {
        var device = new WalletManager(NetworkParams.Mainnet, TempDir(), FastKdf);
        using var wallet = device.Create(Password, "principal");
        var oldBackup = File.ReadAllText(wallet.FilePath);
        var addresses = Enumerable.Range(0, 30).Select(_ => device.NewReceiveAddress(wallet)).ToList();
        // En la cadena solo hay movimientos en la 5 y en la 23 (dentro del margen de 20 a partir de la 5).
        var used = new HashSet<Address> { addresses[4].Address, addresses[22].Address };

        var other = new WalletManager(NetworkParams.Mainnet, TempDir(), FastKdf);
        using var restored = other.Import(oldBackup, Password);
        Assert.Equal(0, restored.ReceiveIndex);
        Assert.True(other.DiscoverAddresses(restored, used.Contains));
        Assert.Equal(23, restored.ReceiveIndex);
        Assert.True(restored.IsMine(addresses[22].Address));
        Assert.False(restored.IsMine(addresses[29].Address));
        Assert.False(other.DiscoverAddresses(restored, used.Contains));
        Assert.Equal(23, WalletFile.FromJson(File.ReadAllText(restored.FilePath)).ReceiveIndex);
    }

    [Fact]
    public void TransfersFromReceiveAddressesAreSignedByTheirOwnKey()
    {
        var device = new WalletManager(NetworkParams.Mainnet, TempDir(), FastKdf);
        using var wallet = device.Create(Password, "principal");
        var receive = device.NewReceiveAddress(wallet);
        using var other = TestChain.NewKey();
        var tx = wallet.CreateTransfer(NetworkParams.Mainnet, receive.Address, TestChain.AddressOf(other), Money.Coin, 1000, 0, 0);
        Assert.True(tx.VerifySignature());
        Assert.Equal(receive.Address, tx.Sender);

        Assert.Throws<InvalidOperationException>(() =>
            wallet.CreateTransfer(NetworkParams.Mainnet, TestChain.AddressOf(other), TestChain.AddressOf(other), 1, 1000, 0, 0));
        wallet.Lock();
        Assert.True(wallet.IsLocked);
        Assert.Throws<InvalidOperationException>(() =>
            wallet.CreateTransfer(NetworkParams.Mainnet, receive.Address, TestChain.AddressOf(other), 1, 1000, 0, 0));
    }

    [Fact]
    public void FilesWithoutOrWithTamperedReceiveIndexStillDecrypt()
    {
        var device = new WalletManager(NetworkParams.Mainnet, TempDir(), FastKdf);
        using var wallet = device.Create(Password, "principal");
        device.NewReceiveAddress(wallet);

        var node = JsonNode.Parse(File.ReadAllText(wallet.FilePath))!.AsObject();
        node.Remove("receiveIndex");
        var legacy = WalletFile.FromJson(node.ToJsonString());
        Assert.Equal(0, legacy.ReceiveIndex);
        var seed = legacy.Decrypt(Password, NetworkParams.Mainnet);
        Array.Clear(seed);

        node["receiveIndex"] = 7;
        var seed2 = WalletFile.FromJson(node.ToJsonString()).Decrypt(Password, NetworkParams.Mainnet);
        Array.Clear(seed2);

        node["receiveIndex"] = -1;
        Assert.Throws<WalletFormatException>(() => WalletFile.FromJson(node.ToJsonString()));
    }

    [Fact]
    public void PaymentPlanPrefersASingleAddress()
    {
        static AddressBalance B(int index, long available) =>
            new(new WalletAddress(index, Address.FromHash(Enumerable.Repeat((byte)(index + 1), Address.HashSize).ToArray()), $"a{index}"), available, 0, 0, 0);
        const long fee = 1000;
        var balances = new[] { B(0, 50_000), B(1, 12_000), B(2, 30_000) };

        var single = WalletBalances.Plan(balances, 10_000, fee)!;
        Assert.Single(single);
        Assert.Equal(1, single[0].From.Index);

        var multi = WalletBalances.Plan(balances, 70_000, fee)!;
        Assert.Equal(2, multi.Count);
        Assert.Equal(70_000, multi.Sum(p => p.Amount));
        Assert.Equal([0, 2], multi.Select(p => p.From.Index));

        Assert.Null(WalletBalances.Plan(balances, WalletBalances.MaxSendable(balances, fee) + 1, fee));
        Assert.NotNull(WalletBalances.Plan(balances, WalletBalances.MaxSendable(balances, fee), fee));
        Assert.Null(WalletBalances.Plan(balances, 0, fee));
    }

    public void Dispose()
    {
        foreach (var dir in _dirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
