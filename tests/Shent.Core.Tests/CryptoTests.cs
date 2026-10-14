using Shent.Core.Consensus;
using Shent.Core.Crypto;
using Shent.Core.Wallet;

namespace Shent.Core.Tests;

public class Bech32mTests
{
    [Theory]
    [InlineData("A1LQFN3A")]
    [InlineData("a1lqfn3a")]
    [InlineData("an83characterlonghumanreadablepartthatcontainsthetheexcludedcharactersbioandnumber11sg7hg6")]
    [InlineData("abcdef1l7aum6echk45nj3s0wdvt2fg8x9yrzpqzd3ryx")]
    [InlineData("split1checkupstagehandshakeupstreamerranterredcaperredlc445v")]
    [InlineData("?1v759aa")]
    public void AcceptsBip350ValidVectors(string vector)
    {
        Assert.True(Bech32m.TryDecode(vector, out _, out _));
    }

    [Theory]
    [InlineData("A1G7SGD8")] // checksum Bech32 (no Bech32m)
    [InlineData("1xj0phk")] // HRP vacío
    [InlineData("pzry9x0s0muk")] // sin separador
    [InlineData("y1b0jsk6g")] // carácter inválido
    [InlineData("lt1igcx5c0")] // carácter de datos inválido
    [InlineData("in1muywd")] // checksum demasiado corto
    [InlineData("M1VUXWEZ")] // checksum calculado con HRP en mayúsculas
    [InlineData("16plkw9")] // HRP vacío
    [InlineData("1p2gdwpf")] // HRP vacío
    [InlineData("aBcDeF1l7aum6echk45nj3s0wdvt2fg8x9yrzpqzd3ryx")] // mayúsculas y minúsculas mezcladas
    public void RejectsInvalidVectors(string vector)
    {
        Assert.False(Bech32m.TryDecode(vector, out _, out _));
    }
}

public class AddressTests
{
    [Fact]
    public void RoundTripsAndDerivesDeterministically()
    {
        using var key = TestChain.NewKey();
        var address = TestChain.AddressOf(key);
        var encoded = address.Encode(NetworkParams.Mainnet);

        Assert.StartsWith("shent1q", encoded);
        Assert.Equal(address, Address.Parse(encoded, "shent"));
        Assert.Equal(address, TestChain.AddressOf(key));
    }

    [Fact]
    public void DetectsTypos()
    {
        using var key = TestChain.NewKey();
        var encoded = TestChain.AddressOf(key).Encode(NetworkParams.Mainnet);
        var chars = encoded.ToCharArray();
        var i = chars.Length - 10;
        chars[i] = chars[i] == 'q' ? 'p' : 'q';
        Assert.False(Address.TryParse(new string(chars), "shent", out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void RejectsAddressFromAnotherNetwork()
    {
        using var key = TestChain.NewKey();
        var testnet = TestChain.AddressOf(key).Encode(NetworkParams.Testnet);
        Assert.False(Address.TryParse(testnet, "shent", out _, out var error));
        Assert.Contains("otra red", error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("shent1")]
    [InlineData("' OR 1=1 --")]
    [InlineData("bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4")]
    public void RejectsMalformedInput(string input)
    {
        Assert.False(Address.TryParse(input, "shent", out _, out _));
    }

    [Fact]
    public void PseudonymIsStableAndDoesNotContainKeyMaterial()
    {
        using var key = TestChain.NewKey();
        var address = TestChain.AddressOf(key);
        var p1 = Pseudonym.FromAddress(address);
        var p2 = Pseudonym.FromAddress(address);
        Assert.Equal(p1, p2);
        Assert.Matches("^[A-Za-z]+-[0-9A-Z]{4}$", p1);
    }
}

public class MoneyTests
{
    [Theory]
    [InlineData("1", 100_000_000)]
    [InlineData("0.00000001", 1)]
    [InlineData("0,5", 50_000_000)]
    [InlineData("12.34567891", 1_234_567_891)]
    [InlineData("100000000", Money.MaxSupply)]
    public void ParsesValidAmounts(string text, long expected)
    {
        Assert.True(Money.TryParse(text, out var units));
        Assert.Equal(expected, units);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1.123456789")]
    [InlineData("abc")]
    [InlineData("1e5")]
    [InlineData("100000000.00000001")]
    [InlineData("99999999999999")]
    [InlineData("1.2.3")]
    public void RejectsManipulatedAmounts(string text)
    {
        Assert.False(Money.TryParse(text, out _));
    }

    [Fact]
    public void FormatsWithoutFloatingPoint()
    {
        Assert.Equal("1.234,5", Money.Format(123_450_000_000));
        Assert.Equal("0,00000001", Money.Format(1));
        Assert.Equal("100.000.000", Money.Format(Money.MaxSupply));
    }
}

public class SignatureTests
{
    [Fact]
    public void SignedTransferVerifiesAndSerializesRoundTrip()
    {
        using var key = TestChain.NewKey();
        using var other = TestChain.NewKey();
        var tx = Model.Transaction.CreateTransfer(key, NetworkParams.Mainnet.Magic, TestChain.AddressOf(other), 5 * Money.Coin, 1000, 0, 0, "hola"u8.ToArray());

        Assert.True(tx.VerifySignature());
        var copy = Model.Transaction.Deserialize(tx.Serialize());
        Assert.Equal(tx.Id, copy.Id);
        Assert.True(copy.VerifySignature());
        Assert.Equal("hola", copy.MemoText);
    }

    [Fact]
    public void TamperedAmountInvalidatesSignature()
    {
        using var key = TestChain.NewKey();
        using var other = TestChain.NewKey();
        var tx = Model.Transaction.CreateTransfer(key, NetworkParams.Mainnet.Magic, TestChain.AddressOf(other), 5 * Money.Coin, 1000, 0);
        var tampered = new Model.Transaction
        {
            Kind = tx.Kind,
            NetworkMagic = tx.NetworkMagic,
            SenderPublicKey = tx.SenderPublicKey,
            Recipient = tx.Recipient,
            Amount = tx.Amount * 100,
            Fee = tx.Fee,
            Nonce = tx.Nonce,
            Signature = tx.Signature,
        };
        Assert.False(tampered.VerifySignature());
    }

    [Fact]
    public void SignatureCannotBeReplayedOnAnotherNetwork()
    {
        using var key = TestChain.NewKey();
        using var other = TestChain.NewKey();
        var tx = Model.Transaction.CreateTransfer(key, NetworkParams.Testnet.Magic, TestChain.AddressOf(other), Money.Coin, 1000, 0);
        var replay = new Model.Transaction
        {
            Kind = tx.Kind,
            NetworkMagic = NetworkParams.Mainnet.Magic,
            SenderPublicKey = tx.SenderPublicKey,
            Recipient = tx.Recipient,
            Amount = tx.Amount,
            Fee = tx.Fee,
            Nonce = tx.Nonce,
            Signature = tx.Signature,
        };
        Assert.False(replay.VerifySignature());
        Assert.NotEqual(tx.Id, replay.Id);
    }

    [Fact]
    public void RejectsWrongKeyAndGarbageSignatures()
    {
        using var key = TestChain.NewKey();
        using var attacker = TestChain.NewKey();
        var message = "SHENT"u8.ToArray();
        var sig = Ed25519Keys.Sign(attacker, message);
        Assert.False(Ed25519Keys.Verify(Ed25519Keys.PublicKeyOf(key), message, sig));
        Assert.False(Ed25519Keys.Verify(Ed25519Keys.PublicKeyOf(key), message, new byte[64]));
        Assert.False(Ed25519Keys.Verify(new byte[31], message, sig));
    }

    [Fact]
    public void MalformedTransactionBytesAreRejected()
    {
        Assert.Throws<FormatException>(() => Model.Transaction.Deserialize(new byte[] { 1, 1, 0, 0 }));
        Assert.Throws<FormatException>(() => Model.Transaction.Deserialize(new byte[] { 9, 9, 9 }));
        Assert.Throws<FormatException>(() => Model.Transaction.Deserialize(new byte[600]));
    }
}

public class TargetTests
{
    [Fact]
    public void CompactRoundTripsNetworkTargets()
    {
        foreach (var network in new[] { NetworkParams.Mainnet, NetworkParams.Testnet, NetworkParams.Regtest })
        {
            var bits = network.InitialBits;
            var target = Target.FromCompact(bits);
            Assert.Equal(bits, Target.ToCompact(target));
            Assert.True(target <= network.PowLimit);
            Assert.True(target > 0);
        }
    }

    [Fact]
    public void LowerTargetMeansMoreWork()
    {
        Assert.True(Target.WorkFor(Target.TwoPow256 / 1000) > Target.WorkFor(Target.TwoPow256 / 10));
    }
}
