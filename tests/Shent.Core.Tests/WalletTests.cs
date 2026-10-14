using System.Text.Json.Nodes;
using Shent.Core.Consensus;
using Shent.Core.Wallet;

namespace Shent.Core.Tests;

public sealed class WalletTests : IDisposable
{
    private static readonly KdfParameters FastKdf = new() { MemoryKiB = WalletFile.MinMemoryKiB, Iterations = 1 };
    private const string Password = "CLAVE-MAESTRA-DE-PRUEBA-1";
    private readonly List<string> _dirs = [];

    private string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "shent-wallet-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        return dir;
    }

    [Fact]
    public void BackupNeverContainsPlainPrivateKey()
    {
        var seed = Crypto.Ed25519Keys.GenerateSeed();
        var file = WalletFile.Encrypt(seed, Password, NetworkParams.Mainnet, "test", FastKdf);
        var json = file.ToJson();
        Assert.DoesNotContain(Convert.ToBase64String(seed), json);
        Assert.DoesNotContain(Convert.ToHexString(seed), json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("shent-wallet", file.Format);
        Assert.Equal(1, file.Version);
        Assert.Equal("argon2id", file.Kdf.Name);
        Assert.Equal("chacha20-poly1305", file.Cipher.Name);
    }

    [Fact]
    public void ExportThenImportRestoresSameAddressOnAnotherDevice()
    {
        var deviceA = new WalletManager(NetworkParams.Mainnet, TempDir(), FastKdf);
        using var created = deviceA.Create(Password, "principal");
        var json = File.ReadAllText(created.FilePath);

        // Otro "dispositivo": carpeta distinta, sin cookies, caché ni estado compartido.
        var deviceB = new WalletManager(NetworkParams.Mainnet, TempDir(), FastKdf);
        using var restored = deviceB.Import(json, Password);

        Assert.Equal(created.Address, restored.Address);
        Assert.Equal(created.EncodedAddress, restored.EncodedAddress);
        Assert.Equal(created.Pseudonym, restored.Pseudonym);
        Assert.Single(deviceB.List());
    }

    [Fact]
    public void WrongPasswordFailsWithGenericError()
    {
        var file = WalletFile.Encrypt(Crypto.Ed25519Keys.GenerateSeed(), Password, NetworkParams.Mainnet, "x", FastKdf);
        var ex = Assert.Throws<WalletPasswordException>(() => file.Decrypt("contraseña-equivocada", NetworkParams.Mainnet));
        Assert.Equal("Contraseña incorrecta o archivo dañado.", ex.Message);
    }

    [Fact]
    public void TamperedMetadataIsDetected()
    {
        var file = WalletFile.Encrypt(Crypto.Ed25519Keys.GenerateSeed(), Password, NetworkParams.Mainnet, "x", FastKdf);
        var otherAddress = WalletFile.Encrypt(Crypto.Ed25519Keys.GenerateSeed(), Password, NetworkParams.Mainnet, "y", FastKdf).Address;

        var swapped = file with { Address = otherAddress };
        Assert.Throws<WalletPasswordException>(() => swapped.Decrypt(Password, NetworkParams.Mainnet));

        var renamed = file with { Pseudonym = "Impostor-0000" };
        Assert.Throws<WalletPasswordException>(() => renamed.Decrypt(Password, NetworkParams.Mainnet));
    }

    [Fact]
    public void TamperedCiphertextIsDetected()
    {
        var file = WalletFile.Encrypt(Crypto.Ed25519Keys.GenerateSeed(), Password, NetworkParams.Mainnet, "x", FastKdf);
        var bytes = Convert.FromBase64String(file.Cipher.Ciphertext);
        bytes[0] ^= 0xFF;
        var tampered = file with { Cipher = file.Cipher with { Ciphertext = Convert.ToBase64String(bytes) } };
        Assert.Throws<WalletPasswordException>(() => tampered.Decrypt(Password, NetworkParams.Mainnet));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("no es json")]
    [InlineData("{\"format\":\"otra-cosa\",\"version\":1}")]
    public void RejectsMalformedFiles(string json)
    {
        Assert.Throws<WalletFormatException>(() => WalletFile.FromJson(json));
    }

    [Fact]
    public void RejectsUnsupportedVersionAndUnsafeKdfParameters()
    {
        var file = WalletFile.Encrypt(Crypto.Ed25519Keys.GenerateSeed(), Password, NetworkParams.Mainnet, "x", FastKdf);
        var node = JsonNode.Parse(file.ToJson())!;
        node["version"] = 99;
        Assert.Throws<WalletFormatException>(() => WalletFile.FromJson(node.ToJsonString()));

        node = JsonNode.Parse(file.ToJson())!;
        node["kdf"]!["memoryKiB"] = 8;
        Assert.Throws<WalletFormatException>(() => WalletFile.FromJson(node.ToJsonString()));

        node = JsonNode.Parse(file.ToJson())!;
        node["kdf"]!["memoryKiB"] = 64 * 1024 * 1024;
        Assert.Throws<WalletFormatException>(() => WalletFile.FromJson(node.ToJsonString()));
    }

    [Fact]
    public void RejectsBackupFromAnotherNetwork()
    {
        var file = WalletFile.Encrypt(Crypto.Ed25519Keys.GenerateSeed(), Password, NetworkParams.Testnet, "x", FastKdf);
        Assert.Throws<WalletFormatException>(() => file.Decrypt(Password, NetworkParams.Mainnet));
    }

    [Fact]
    public void RejectsShortPasswords()
    {
        Assert.Throws<ArgumentException>(() => WalletFile.Encrypt(Crypto.Ed25519Keys.GenerateSeed(), "corta", NetworkParams.Mainnet, "x", FastKdf));
    }

    [Fact]
    public void GeneratedMasterPasswordsAreStrongAndUnique()
    {
        var a = PasswordPolicy.GenerateMasterPassword();
        var b = PasswordPolicy.GenerateMasterPassword();
        Assert.NotEqual(a, b);
        Assert.Matches("^([2-9A-Z]{5}-){4}[2-9A-Z]{5}$", a);
        Assert.True(a.Length >= PasswordPolicy.MinLength);
    }

    [Fact]
    public void FiveFailuresTriggerGrowingLocalDelayButNeverPermanentLock()
    {
        var now = DateTimeOffset.UtcNow;
        var throttle = new UnlockThrottle(null, () => now);
        for (var i = 0; i < 4; i++) throttle.RecordFailure("w");
        Assert.Equal(TimeSpan.Zero, throttle.RemainingWait("w"));

        throttle.RecordFailure("w");
        var first = throttle.RemainingWait("w");
        Assert.True(first > TimeSpan.Zero);

        throttle.RecordFailure("w");
        Assert.True(throttle.RemainingWait("w") > first);

        now = now.AddHours(1);
        Assert.Equal(TimeSpan.Zero, throttle.RemainingWait("w"));
        throttle.RecordSuccess("w");
        Assert.Equal(0, throttle.Failures("w"));
    }

    [Fact]
    public void ManagerAppliesThrottleAfterRepeatedWrongPasswords()
    {
        var manager = new WalletManager(NetworkParams.Mainnet, TempDir(), FastKdf);
        string path;
        using (var wallet = manager.Create(Password, "x")) path = wallet.FilePath;

        for (var i = 0; i < UnlockThrottle.FreeAttempts; i++)
        {
            Assert.Throws<WalletPasswordException>(() => manager.Unlock(path, "incorrecta-" + i));
        }
        Assert.Throws<WalletLockedOutException>(() => manager.Unlock(path, Password));
    }

    [Fact]
    public void LockedWalletCannotSign()
    {
        var manager = new WalletManager(NetworkParams.Mainnet, TempDir(), FastKdf);
        var wallet = manager.Create(Password, "x");
        wallet.Lock();
        using var other = TestChain.NewKey();
        Assert.Throws<InvalidOperationException>(() =>
            wallet.CreateTransfer(NetworkParams.Mainnet, TestChain.AddressOf(other), 1, 1000, 0, 0));
    }

    public void Dispose()
    {
        foreach (var dir in _dirs)
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}
