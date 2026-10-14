using Shent.Core.Chain;
using Shent.Core.Consensus;
using Shent.Core.Crypto;
using Shent.Core.Model;

namespace Shent.Core.Mempool;

public sealed record MempoolResult(bool Accepted, string? Error = null, bool Punishable = false)
{
    public static readonly MempoolResult Ok = new(true);
    public static MempoolResult Reject(string error, bool punishable = false) => new(false, error, punishable);
}

/// <summary>
/// Transacciones válidas pendientes de minar. Reglas de política (no de consenso):
/// comisión mínima, nonces contiguos por remitente, sin reemplazo de transacciones y límites de tamaño.
///
/// Las transacciones "privadas" están en la fase de tallo de Dandelion++: cuentan para nonces y saldos,
/// pero no se anuncian, no se entregan a quien las pida y no se minan hasta que se difunden; si no,
/// cualquier par podría preguntar por ellas y descubrir de qué nodo salieron.
/// </summary>
public sealed class TxMempool
{
    public const int MaxTransactions = 5000;
    public const int MaxPerSender = 64;

    private readonly object _gate = new();
    private readonly Blockchain _chain;
    private readonly Dictionary<Hash256, Transaction> _byId = new();
    private readonly Dictionary<Address, SortedList<long, Transaction>> _bySender = new();
    private readonly HashSet<Hash256> _private = new();

    public event Action? Changed;

    public TxMempool(Blockchain chain)
    {
        _chain = chain;
        _chain.ChainUpdated += OnChainUpdated;
    }

    public int Count
    {
        get { lock (_gate) return _byId.Count; }
    }

    /// <summary>Verdadero también para transacciones privadas (sirve para no procesarlas dos veces).</summary>
    public bool Contains(Hash256 txid)
    {
        lock (_gate) return _byId.ContainsKey(txid);
    }

    /// <summary>Transacción pública por id; las privadas se tratan como inexistentes.</summary>
    public Transaction? Get(Hash256 txid)
    {
        lock (_gate) return _private.Contains(txid) ? null : _byId.GetValueOrDefault(txid);
    }

    public bool IsPrivate(Hash256 txid)
    {
        lock (_gate) return _private.Contains(txid);
    }

    public Transaction? GetPrivate(Hash256 txid)
    {
        lock (_gate) return _private.Contains(txid) ? _byId.GetValueOrDefault(txid) : null;
    }

    public MempoolResult TryAdd(Transaction tx, bool isPrivate = false)
    {
        MempoolResult result;
        lock (_gate)
        {
            result = TryAddLocked(tx);
            if (result.Accepted && isPrivate) _private.Add(tx.Id);
        }
        if (result.Accepted) Changed?.Invoke();
        return result;
    }

    /// <summary>Pasa una transacción privada a pública. Devuelve falso si no estaba o ya era pública.</summary>
    public bool MakePublic(Hash256 txid)
    {
        lock (_gate) return _private.Remove(txid);
    }

    public List<Hash256> GetPrivateIds()
    {
        lock (_gate) return _private.ToList();
    }

    private MempoolResult TryAddLocked(Transaction tx)
    {
        var network = _chain.Network;
        if (tx.IsCoinbase) return MempoolResult.Reject("Las coinbase no se transmiten sueltas.", true);
        if (tx.NetworkMagic != network.Magic) return MempoolResult.Reject("Transacción de otra red.", true);
        if (_byId.ContainsKey(tx.Id)) return MempoolResult.Reject("La transacción ya está pendiente.");
        if (_chain.GetTransaction(tx.Id) is not null) return MempoolResult.Reject("La transacción ya está confirmada.");

        try
        {
            Blockchain.CheckTransactionRanges(tx, network);
        }
        catch (ConsensusException ex)
        {
            return MempoolResult.Reject(ex.Message, true);
        }
        if (tx.Fee < network.MinRelayFee)
            return MempoolResult.Reject($"Comisión inferior a la mínima ({Money.Format(network.MinRelayFee)} SHENT).");
        if (!tx.VerifySignature()) return MempoolResult.Reject("Firma inválida.", true);

        var sender = tx.Sender;
        if (sender.Equals(tx.Recipient)) return MempoolResult.Reject("No se permiten transferencias a la misma dirección.");

        var tipHeight = _chain.Height;
        if (tx.ExpiresAtHeight != 0 && tx.ExpiresAtHeight <= tipHeight)
            return MempoolResult.Reject("La transacción ya expiró.");

        var account = _chain.GetAccount(sender);
        _bySender.TryGetValue(sender, out var pending);
        var pendingCount = pending?.Count ?? 0;
        if (tx.Nonce < account.Nonce) return MempoolResult.Reject("Nonce ya utilizado (posible reenvío o doble gasto).");
        if (pending is not null && pending.ContainsKey(tx.Nonce))
            return MempoolResult.Reject("Ya hay otra transacción pendiente con ese nonce (doble gasto rechazado).");
        if (tx.Nonce != account.Nonce + pendingCount)
            return MempoolResult.Reject($"Nonce fuera de secuencia: se esperaba {account.Nonce + pendingCount}.");
        if (pendingCount >= MaxPerSender) return MempoolResult.Reject("Demasiadas transacciones pendientes para esta dirección.");
        if (_byId.Count >= MaxTransactions) return MempoolResult.Reject("El mempool está lleno; probá con una comisión mayor más tarde.");

        long committed = 0;
        if (pending is not null)
        {
            foreach (var p in pending.Values) committed = checked(committed + p.Amount + p.Fee);
        }
        if (checked(committed + tx.Amount + tx.Fee) > account.Balance)
            return MempoolResult.Reject("Saldo insuficiente.");

        if (pending is null)
        {
            pending = new SortedList<long, Transaction>();
            _bySender[sender] = pending;
        }
        pending.Add(tx.Nonce, tx);
        _byId[tx.Id] = tx;
        return MempoolResult.Ok;
    }

    private void OnChainUpdated(ChainUpdate update)
    {
        lock (_gate)
        {
            var candidates = _byId.Values.ToList();
            foreach (var block in update.Disconnected)
            {
                candidates.AddRange(block.Transactions.Where(t => !t.IsCoinbase));
            }
            var confirmed = new HashSet<Hash256>(update.Connected.SelectMany(b => b.Transactions).Select(t => t.Id));

            _byId.Clear();
            _bySender.Clear();
            foreach (var tx in candidates
                         .Where(t => !confirmed.Contains(t.Id))
                         .DistinctBy(t => t.Id)
                         .OrderBy(t => t.Sender.ToHex(), StringComparer.Ordinal)
                         .ThenBy(t => t.Nonce))
            {
                TryAddLocked(tx);
            }
            _private.RemoveWhere(id => !_byId.ContainsKey(id));
        }
        Changed?.Invoke();
    }

    /// <summary>Selecciona transacciones para un bloque priorizando comisión y respetando el orden de nonces por remitente.</summary>
    public List<Transaction> SelectForBlock(int maxBytes, int maxCount = Block.MaxTransactions - 1)
    {
        lock (_gate)
        {
            // Por remitente, solo el prefijo público: una transacción privada corta la cola (las siguientes dependen de su nonce).
            var queues = _bySender.Values
                .Select(list => new Queue<Transaction>(list.Values.TakeWhile(t => !_private.Contains(t.Id))))
                .Where(q => q.Count > 0)
                .ToList();
            var selected = new List<Transaction>();
            var bytes = 0;
            while (queues.Count > 0 && selected.Count < maxCount)
            {
                var best = queues.MaxBy(q => q.Peek().Fee)!;
                var tx = best.Peek();
                if (bytes + tx.SerializedSize + 4 > maxBytes) break;
                selected.Add(best.Dequeue());
                bytes += tx.SerializedSize + 4;
                if (best.Count == 0) queues.Remove(best);
            }
            return selected;
        }
    }

    /// <summary>Transacciones públicas (lo que se puede anunciar a otros nodos).</summary>
    public List<Transaction> GetAll()
    {
        lock (_gate) return _byId.Values.Where(t => !_private.Contains(t.Id)).ToList();
    }

    /// <summary>Todas, incluidas las privadas: solo para mostrar al usuario sus propios movimientos pendientes.</summary>
    public List<Transaction> GetAllIncludingPrivate()
    {
        lock (_gate) return _byId.Values.ToList();
    }

    /// <summary>Próximo nonce a usar por una dirección considerando sus transacciones pendientes.</summary>
    public long NextNonce(Address sender)
    {
        lock (_gate)
        {
            var account = _chain.GetAccount(sender);
            return account.Nonce + (_bySender.TryGetValue(sender, out var pending) ? pending.Count : 0);
        }
    }

    public (long Outgoing, long Incoming) PendingTotals(Address address)
    {
        lock (_gate)
        {
            long outgoing = 0, incoming = 0;
            foreach (var tx in _byId.Values)
            {
                if (tx.Recipient.Equals(address)) incoming += tx.Amount;
                if (tx.Sender.Equals(address)) outgoing += tx.Amount + tx.Fee;
            }
            return (outgoing, incoming);
        }
    }
}
