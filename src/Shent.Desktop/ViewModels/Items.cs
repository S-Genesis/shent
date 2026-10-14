using Avalonia.Media;
using Shent.Core.Chain;
using Shent.Core.Consensus;
using Shent.Core.Model;
using Shent.Core.Node;
using Shent.Core.P2P;
using Shent.Desktop.Localization;
using Shent.Desktop.Services;
using Shent.Desktop.UI;

namespace Shent.Desktop.ViewModels;

public sealed record BlockItem(long Height, string Hash, string ShortHash, long Timestamp, int TxCount, string Miner, string MinerShort, string Reward, string Size, bool MinedByMe)
{
    public string HeightText => $"#{Format.Integer(Height)}";
    public string TimeAgo => Format.TimeAgo(Timestamp);
    public string TxText => TxCount == 1 ? Loc.T("1 transacción") : Loc.T("{0} transacciones", Format.Integer(TxCount));

    public static BlockItem From(BlockSummary b, NetworkParams network, Address? mine)
    {
        var miner = b.Miner.Encode(network);
        return new BlockItem(b.Height, b.Hash.ToString(), b.Hash.ToShortString(), b.Timestamp, b.TransactionCount,
            miner, Address.Shorten(miner), Format.Shent(b.Reward + b.Fees), $"{Format.Integer(b.SizeBytes)} B",
            mine is not null && b.Miner.Equals(mine));
    }
}

public enum TxDirection { Incoming, Outgoing, Mined, Self }

public sealed record TxItem(
    string Id, string ShortId, TxDirection Direction, string Title, string Counterparty, string Amount,
    string Fee, string Memo, long Timestamp, long Height, long Confirmations, bool Pending)
{
    public Geometry Icon => Direction switch
    {
        TxDirection.Incoming => Icons.Received,
        TxDirection.Mined => Icons.Flash,
        _ => Icons.Sent,
    };

    public string DirectionClass => Direction switch
    {
        TxDirection.Incoming => "incoming",
        TxDirection.Mined => "mined",
        _ => "outgoing",
    };

    public bool IsIncoming => Direction == TxDirection.Incoming;
    public bool IsMined => Direction == TxDirection.Mined;
    public bool IsOutgoing => Direction is TxDirection.Outgoing or TxDirection.Self;

    public string StatusText => Pending
        ? Loc.T("Pendiente")
        : Confirmations >= 6 ? Loc.T("Confirmada")
        : Confirmations == 1 ? Loc.T("1 confirmación")
        : Loc.T("{0} confirmaciones", Format.Integer(Confirmations));

    public string WhenText => Pending ? Loc.T("En mempool") : Format.TimeAgo(Timestamp);

    public static TxItem FromConfirmed(TxLocation loc, Address me, NetworkParams network) =>
        FromConfirmed(loc, me.Equals, network);

    public static TxItem FromPending(Transaction tx, Address me, NetworkParams network) =>
        FromPending(tx, me.Equals, network);

    /// <param name="isMine">Indica si una dirección es de la billetera (principal o de cobro).</param>
    public static TxItem FromConfirmed(TxLocation loc, Func<Address, bool> isMine, NetworkParams network) =>
        Build(loc.Transaction, isMine, network, loc.Timestamp, loc.Height, loc.Confirmations, pending: false);

    public static TxItem FromPending(Transaction tx, Func<Address, bool> isMine, NetworkParams network) =>
        Build(tx, isMine, network, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 0, 0, pending: true);

    private static TxItem Build(Transaction tx, Func<Address, bool> isMine, NetworkParams network, long timestamp, long height, long confirmations, bool pending)
    {
        TxDirection direction;
        string counterparty;
        long signedAmount;
        if (tx.IsCoinbase)
        {
            direction = TxDirection.Mined;
            counterparty = Loc.T("Recompensa de bloque");
            signedAmount = tx.Amount;
        }
        else
        {
            var fromMe = isMine(tx.Sender);
            var toMe = isMine(tx.Recipient);
            if (fromMe && toMe)
            {
                direction = TxDirection.Self;
                counterparty = Loc.T("A mí mismo");
                signedAmount = -tx.Fee;
            }
            else if (fromMe)
            {
                direction = TxDirection.Outgoing;
                counterparty = Address.Shorten(tx.Recipient.Encode(network));
                signedAmount = -(tx.Amount + tx.Fee);
            }
            else
            {
                direction = TxDirection.Incoming;
                counterparty = Address.Shorten(tx.Sender.Encode(network));
                signedAmount = tx.Amount;
            }
        }

        var title = direction switch
        {
            TxDirection.Incoming => Loc.T("Recibido"),
            TxDirection.Outgoing => Loc.T("Enviado"),
            TxDirection.Mined => Loc.T("Minado · bloque {0}", Format.Integer(height)),
            _ => Loc.T("Transferencia propia"),
        };
        var amount = (signedAmount >= 0 ? "+" : "−") + Format.Shent(Math.Abs(signedAmount));
        var memo = tx.Memo.Length > 0 ? System.Text.Encoding.UTF8.GetString(tx.Memo) : "";
        return new TxItem(tx.Id.ToString(), tx.Id.ToShortString(), direction, title, counterparty, amount,
            tx.IsCoinbase ? "" : Format.Shent(tx.Fee), memo, timestamp, height, confirmations, pending);
    }
}

public sealed record LogItem(string Time, string Category, string Message, string Level)
{
    public static LogItem From(LogEntry e) => new(
        e.Time.ToLocalTime().ToString("HH:mm:ss"),
        e.Category,
        e.Message,
        e.Level switch
        {
            LogLevel.Success => "ok",
            LogLevel.Warning => "warn",
            LogLevel.Error => "bad",
            _ => "info",
        });
}

public sealed record PeerItem(Guid Id, string Address, bool Inbound, string UserAgent, string Height, string Ping, string Since, bool Syncing,
    string PeerIdShort = "", bool Relayed = false)
{
    public string Direction => (Inbound, Relayed) switch
    {
        (true, true) => Loc.T("Entrante · relay"),
        (true, false) => Loc.T("Entrante"),
        (false, true) => Loc.T("Saliente · relay"),
        (false, false) => Loc.T("Saliente"),
    };

    public string Subtitle => string.IsNullOrEmpty(PeerIdShort) ? UserAgent : $"{PeerIdShort} · {UserAgent}";

    public static PeerItem From(PeerSnapshot p) => new(
        p.Id, p.Address, p.Inbound, string.IsNullOrWhiteSpace(p.UserAgent) ? Loc.T("desconocido") : p.UserAgent,
        Format.Integer(p.BestHeight),
        p.PingMs is { } ms ? $"{Format.Decimal(ms, 0)} ms" : "—",
        Format.Duration((DateTimeOffset.UtcNow - p.ConnectedAt).TotalSeconds),
        p.Syncing,
        PeerId.TryParse(p.PeerId, out var id) ? id!.Short : "",
        p.Relayed);
}

public sealed record FoundBlockItem(long Height, string ShortHash, string Reward, DateTimeOffset FoundAt)
{
    public string HeightText => Loc.T("Bloque #{0}", Format.Integer(Height));
    public string When => FoundAt.ToLocalTime().ToString("HH:mm:ss");
}
