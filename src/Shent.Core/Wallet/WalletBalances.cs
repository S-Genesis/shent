using Shent.Core.Chain;
using Shent.Core.Mempool;

namespace Shent.Core.Wallet;

public sealed record AddressBalance(WalletAddress Address, long Balance, long PendingOut, long PendingIn, long Nonce)
{
    public long Available => Math.Max(0, Balance - PendingOut);
}

public sealed record WalletTotals(long Balance, long Available, long PendingIn, long PendingOut, long Sent, IReadOnlyList<AddressBalance> PerAddress);

/// <summary>Una transferencia del plan de pago: desde qué dirección propia y cuánto.</summary>
public sealed record PaymentPart(WalletAddress From, long Amount);

/// <summary>Saldos sumados de todas las direcciones de la billetera y armado de pagos.</summary>
public static class WalletBalances
{
    public static WalletTotals Compute(UnlockedWallet wallet, Blockchain chain, TxMempool mempool)
    {
        var list = new List<AddressBalance>();
        foreach (var address in wallet.Addresses)
        {
            var account = chain.GetAccount(address.Address);
            var (outgoing, incoming) = mempool.PendingTotals(address.Address);
            list.Add(new AddressBalance(address, account.Balance, outgoing, incoming, account.Nonce));
        }
        return new WalletTotals(
            list.Sum(a => a.Balance),
            list.Sum(a => a.Available),
            list.Sum(a => a.PendingIn),
            list.Sum(a => a.PendingOut),
            list.Sum(a => a.Nonce),
            list);
    }

    /// <summary>Máximo que se puede enviar pagando una comisión por cada dirección que aporte fondos.</summary>
    public static long MaxSendable(IEnumerable<AddressBalance> balances, long fee) =>
        balances.Where(b => b.Available > fee).Sum(b => b.Available - fee);

    /// <summary>
    /// Arma el pago con la menor cantidad posible de direcciones propias: en el modelo de cuentas cada
    /// dirección firma su propia transacción, y gastar de varias a la vez permite que un observador las
    /// vincule. Si alcanza una sola, se usa la de menor saldo suficiente (deja intactas las grandes).
    /// Devuelve null si el saldo no alcanza.
    /// </summary>
    public static List<PaymentPart>? Plan(IEnumerable<AddressBalance> balances, long amount, long fee)
    {
        if (amount <= 0) return null;
        var usable = balances.Where(b => b.Available > fee).ToList();
        var single = usable.Where(b => b.Available >= amount + fee).MinBy(b => b.Available);
        if (single is not null) return [new PaymentPart(single.Address, amount)];

        var parts = new List<PaymentPart>();
        var remaining = amount;
        foreach (var b in usable.OrderByDescending(b => b.Available))
        {
            var take = Math.Min(remaining, b.Available - fee);
            parts.Add(new PaymentPart(b.Address, take));
            remaining -= take;
            if (remaining == 0) return parts;
        }
        return null;
    }
}
