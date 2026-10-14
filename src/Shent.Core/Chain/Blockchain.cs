using System.Numerics;
using Shent.Core.Consensus;
using Shent.Core.Crypto;
using Shent.Core.Encoding;
using Shent.Core.Model;

namespace Shent.Core.Chain;

/// <summary>
/// Cadena de bloques local de un nodo. Responsabilidades:
/// validar cada bloque recibido, mantener el árbol de bloques conocidos, seleccionar la cadena
/// válida con mayor trabajo acumulado y aplicar (o revertir) su efecto sobre los saldos.
/// Toda reorganización se ejecuta dentro de una única transacción SQLite: o se aplica completa o no se aplica.
/// </summary>
public sealed class Blockchain : IDisposable
{
    private const int MaxInvalidCache = 10_000;

    private readonly object _gate = new();
    private readonly ChainStore _store;
    private readonly PowHasher _pow;
    private readonly Func<long> _clock;
    private readonly Dictionary<Hash256, BlockIndex> _index = new();
    private readonly HashSet<Hash256> _invalid = new();
    private BlockIndex _tip;
    private long _issued;

    public NetworkParams Network { get; }
    public Hash256 GenesisHash { get; }

    /// <summary>Se dispara (fuera del candado interno) cada vez que cambia la punta de la cadena.</summary>
    public event Action<ChainUpdate>? ChainUpdated;

    public Blockchain(NetworkParams network, string databasePath, Func<long>? clock = null)
    {
        Network = network;
        _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        _pow = network.CreatePowHasher();
        _store = new ChainStore(databasePath);

        var genesis = network.CreateGenesisBlock();
        GenesisHash = genesis.Hash;

        var stored = _store.LoadIndex();
        if (stored.Count == 0)
        {
            InitializeGenesis(genesis);
        }
        else
        {
            foreach (var entry in stored)
            {
                BlockIndex? prev = null;
                if (entry.Height > 0 && !_index.TryGetValue(entry.PrevHash, out prev))
                {
                    continue;
                }
                _index[entry.Hash] = new BlockIndex
                {
                    Hash = entry.Hash,
                    Height = entry.Height,
                    Prev = prev,
                    Timestamp = entry.Timestamp,
                    Bits = entry.Bits,
                    TotalWork = entry.TotalWork,
                    Status = entry.Status,
                };
            }
            if (!_index.TryGetValue(GenesisHash, out var g) || g.Height != 0)
            {
                throw new InvalidOperationException("La base de datos pertenece a otra red o a otra versión del génesis.");
            }
        }

        var tipBytes = _store.GetMeta("tip") ?? throw new InvalidOperationException("Base de datos sin punta de cadena.");
        _tip = _index[new Hash256(tipBytes)];
        _issued = _store.GetMetaLong("issued") ?? 0;
    }

    private void InitializeGenesis(Block genesis)
    {
        var work = Target.WorkForCompact(genesis.Header.Bits);
        _store.BeginTransaction();
        _store.PutBlock(genesis.Hash, 0, Hash256.Zero, genesis.Header.Timestamp, genesis.Header.Bits, work, BlockStatus.Valid, genesis.Serialize());
        _store.SetMainChain(0, genesis.Hash);
        _store.SetMeta("tip", genesis.Hash.ToArray());
        _store.SetMetaLong("issued", 0);
        _store.Commit();
        _index[genesis.Hash] = new BlockIndex
        {
            Hash = genesis.Hash,
            Height = 0,
            Prev = null,
            Timestamp = genesis.Header.Timestamp,
            Bits = genesis.Header.Bits,
            TotalWork = work,
            Status = BlockStatus.Valid,
        };
    }

    public BlockIndex Tip
    {
        get { lock (_gate) return _tip; }
    }

    public long Height => Tip.Height;

    public long IssuedSupply
    {
        get { lock (_gate) return _issued; }
    }

    public bool HasBlock(Hash256 hash)
    {
        lock (_gate) return _index.ContainsKey(hash) || _invalid.Contains(hash);
    }

    // =====================================================================
    // Aceptación de bloques
    // =====================================================================

    public BlockAcceptResult ProcessBlock(Block block)
    {
        ChainUpdate? update;
        BlockAcceptResult result;
        lock (_gate)
        {
            result = ProcessBlockLocked(block, out update);
        }
        if (update is not null)
        {
            ChainUpdated?.Invoke(update);
        }
        return result;
    }

    private BlockAcceptResult ProcessBlockLocked(Block block, out ChainUpdate? update)
    {
        update = null;
        var hash = block.Hash;

        if (_index.TryGetValue(hash, out var existing))
        {
            return existing.Status == BlockStatus.Invalid
                ? new BlockAcceptResult(BlockAcceptStatus.Invalid, "Bloque previamente marcado como inválido.", true)
                : new BlockAcceptResult(BlockAcceptStatus.AlreadyKnown);
        }
        if (_invalid.Contains(hash))
        {
            return new BlockAcceptResult(BlockAcceptStatus.Invalid, "Bloque previamente marcado como inválido.", true);
        }

        try
        {
            CheckBlockStructure(block);
        }
        catch (ConsensusException ex)
        {
            return new BlockAcceptResult(BlockAcceptStatus.Invalid, ex.Message, ex.Punishable);
        }

        if (!_index.TryGetValue(block.Header.PrevHash, out var parent))
        {
            return new BlockAcceptResult(BlockAcceptStatus.Orphan, "Se desconoce el bloque anterior.");
        }
        if (parent.Status == BlockStatus.Invalid)
        {
            RememberInvalid(hash);
            return new BlockAcceptResult(BlockAcceptStatus.Invalid, "El bloque anterior es inválido.", true);
        }

        try
        {
            CheckContextualHeader(block.Header, hash, parent);
        }
        catch (ConsensusException ex)
        {
            if (ex.Punishable) RememberInvalid(hash);
            return new BlockAcceptResult(BlockAcceptStatus.Invalid, ex.Message, ex.Punishable);
        }

        var fork = FindFork(_tip, parent);
        if (_tip.Height - fork.Height > Network.MaxForkDepth)
        {
            return new BlockAcceptResult(BlockAcceptStatus.Invalid,
                $"La bifurcación supera la profundidad máxima de reorganización ({Network.MaxForkDepth} bloques).", false);
        }
        // Una vez que la cadena propia pasó el último punto de control, no se guardan ramas que se separen antes:
        // nunca podrían ganar (chocarían con el punto de control) y solo servirían para llenar el disco.
        var lastCheckpoint = Network.LastCheckpointHeight;
        if (lastCheckpoint > 0 && _tip.Height >= lastCheckpoint && fork.Height < lastCheckpoint)
        {
            return new BlockAcceptResult(BlockAcceptStatus.Invalid,
                $"La bifurcación empieza antes del último punto de control (bloque {lastCheckpoint}).", false);
        }

        var index = new BlockIndex
        {
            Hash = hash,
            Height = block.Header.Height,
            Prev = parent,
            Timestamp = block.Header.Timestamp,
            Bits = block.Header.Bits,
            TotalWork = parent.TotalWork + Target.WorkForCompact(block.Header.Bits),
            Status = BlockStatus.Stored,
        };
        _store.PutBlock(hash, index.Height, parent.Hash, index.Timestamp, index.Bits, index.TotalWork, BlockStatus.Stored, block.Serialize());
        _index[hash] = index;

        if (index.TotalWork <= _tip.TotalWork)
        {
            return new BlockAcceptResult(BlockAcceptStatus.SideChain);
        }

        if (TryActivate(index, out update, out var reason))
        {
            return new BlockAcceptResult(BlockAcceptStatus.NewTip);
        }
        return new BlockAcceptResult(BlockAcceptStatus.Invalid, reason, true);
    }

    private void RememberInvalid(Hash256 hash)
    {
        if (_invalid.Count >= MaxInvalidCache) _invalid.Clear();
        _invalid.Add(hash);
    }

    /// <summary>Reglas que no dependen de la cadena: formato, Merkle, coinbase, rangos y prueba de trabajo.</summary>
    private void CheckBlockStructure(Block block)
    {
        var header = block.Header;
        if (header.Version != BlockHeader.CurrentVersion)
            throw new ConsensusException("Versión de bloque no soportada.");
        if (header.NetworkMagic != Network.Magic)
            throw new ConsensusException("El bloque pertenece a otra red.");
        if (block.Transactions.Count == 0 || block.Transactions.Count > Block.MaxTransactions)
            throw new ConsensusException("Cantidad de transacciones inválida.");
        if (block.Serialize().Length > Network.MaxBlockBytes)
            throw new ConsensusException("El bloque supera el tamaño máximo.");
        if (!block.Transactions[0].IsCoinbase)
            throw new ConsensusException("La primera transacción debe ser la coinbase.");

        var seen = new HashSet<Hash256>();
        for (var i = 0; i < block.Transactions.Count; i++)
        {
            var tx = block.Transactions[i];
            if (i > 0 && tx.IsCoinbase)
                throw new ConsensusException("Solo puede haber una coinbase.");
            if (tx.NetworkMagic != Network.Magic)
                throw new ConsensusException("Transacción de otra red.");
            if (!seen.Add(tx.Id))
                throw new ConsensusException("Transacción duplicada en el bloque.");
            CheckTransactionRanges(tx, Network);
        }

        if (Block.ComputeMerkleRoot(block.Transactions) != header.MerkleRoot)
            throw new ConsensusException("La raíz Merkle no coincide.");

        BigInteger target;
        try
        {
            target = Target.FromCompact(header.Bits);
        }
        catch (FormatException)
        {
            throw new ConsensusException("Objetivo de dificultad malformado.");
        }
        if (target.Sign <= 0 || target > Network.PowLimit)
            throw new ConsensusException("Objetivo de dificultad fuera de rango.");

        if (!PowHasher.MeetsTarget(_pow.Hash(header.Serialize()), target))
            throw new ConsensusException("Prueba de trabajo insuficiente.");
    }

    /// <summary>Rangos válidos de importes y campos; compartido con el mempool.</summary>
    public static void CheckTransactionRanges(Transaction tx, NetworkParams network)
    {
        if (tx.IsCoinbase)
        {
            if (tx.Amount < 0 || tx.Amount > network.MaxSupply)
                throw new ConsensusException("Importe de coinbase fuera de rango.");
            return;
        }
        if (tx.Amount <= 0 || tx.Amount > network.MaxSupply)
            throw new ConsensusException("Importe fuera de rango.");
        if (tx.Fee < 0 || tx.Fee > network.MaxSupply)
            throw new ConsensusException("Comisión fuera de rango.");
        if (tx.Nonce < 0 || tx.ExpiresAtHeight < 0)
            throw new ConsensusException("Nonce o expiración fuera de rango.");
        if (tx.SenderPublicKey.Length != Ed25519Keys.PublicKeySize || tx.Signature.Length != Ed25519Keys.SignatureSize)
            throw new ConsensusException("Clave pública o firma con longitud inválida.");
    }

    private void CheckContextualHeader(BlockHeader header, Hash256 hash, BlockIndex parent)
    {
        if (header.Height != parent.Height + 1)
            throw new ConsensusException("Altura de bloque incorrecta.");
        var expectedBits = NextBitsLocked(parent);
        if (header.Bits != expectedBits)
            throw new ConsensusException("La dificultad no coincide con la exigida por la red.");
        if (header.Timestamp <= MedianTimePastLocked(parent))
            throw new ConsensusException("Marca de tiempo anterior a la mediana de los últimos bloques.");
        if (header.Timestamp > _clock() + Network.MaxFutureDriftSeconds)
            throw new ConsensusException("Marca de tiempo demasiado lejana en el futuro (revisá el reloj del sistema).", punishable: false);
        if (Network.Checkpoints.TryGetValue(header.Height, out var checkpoint) && checkpoint != hash)
            throw new ConsensusException("El bloque contradice un punto de control.");
    }

    private static BlockIndex FindFork(BlockIndex a, BlockIndex b)
    {
        while (a.Height > b.Height) a = a.Prev!;
        while (b.Height > a.Height) b = b.Prev!;
        while (a.Hash != b.Hash)
        {
            a = a.Prev!;
            b = b.Prev!;
        }
        return a;
    }

    private bool TryActivate(BlockIndex target, out ChainUpdate? update, out string? reason)
    {
        update = null;
        reason = null;
        var fork = FindFork(_tip, target);

        var toDisconnect = new List<BlockIndex>();
        for (var cur = _tip; cur.Hash != fork.Hash; cur = cur.Prev!) toDisconnect.Add(cur);

        var toConnect = new List<BlockIndex>();
        for (var cur = target; cur.Hash != fork.Hash; cur = cur.Prev!) toConnect.Add(cur);
        toConnect.Reverse();

        var issued = _issued;
        var connected = new List<Block>();
        var disconnected = new List<Block>();
        BlockIndex? failed = null;

        _store.BeginTransaction();
        try
        {
            foreach (var idx in toDisconnect)
            {
                var block = LoadBlockLocked(idx.Hash);
                DisconnectBlock(idx, ref issued);
                disconnected.Add(block);
            }
            foreach (var idx in toConnect)
            {
                var block = LoadBlockLocked(idx.Hash);
                failed = idx;
                ConnectBlock(idx, block, ref issued);
                connected.Add(block);
            }
            failed = null;
            _store.SetMeta("tip", target.Hash.ToArray());
            _store.SetMetaLong("issued", issued);
            _store.Commit();
        }
        catch (ConsensusException ex)
        {
            _store.Rollback();
            if (failed is not null) MarkInvalid(failed);
            reason = ex.Message;
            return false;
        }
        catch
        {
            _store.Rollback();
            throw;
        }

        foreach (var idx in toConnect) idx.Status = BlockStatus.Valid;
        _tip = target;
        _issued = issued;
        update = new ChainUpdate(target, connected, disconnected);
        return true;
    }

    private void MarkInvalid(BlockIndex bad)
    {
        bad.Status = BlockStatus.Invalid;
        _store.SetBlockStatus(bad.Hash, BlockStatus.Invalid);
        foreach (var idx in _index.Values)
        {
            if (idx.Height > bad.Height && idx.GetAncestor(bad.Height)?.Hash == bad.Hash)
            {
                idx.Status = BlockStatus.Invalid;
                _store.SetBlockStatus(idx.Hash, BlockStatus.Invalid);
            }
        }
    }

    /// <summary>Aplica un bloque sobre el estado actual. Debe llamarse dentro de una transacción abierta.</summary>
    private void ConnectBlock(BlockIndex idx, Block block, ref long issued)
    {
        var height = idx.Height;
        var touched = new Dictionary<Address, (AccountState Original, bool Existed, AccountState Current)>();

        AccountState Get(Address address)
        {
            if (touched.TryGetValue(address, out var entry)) return entry.Current;
            var state = _store.GetAccount(address, out var exists);
            touched[address] = (state, exists, state);
            return state;
        }

        void Set(Address address, AccountState state)
        {
            var entry = touched[address];
            touched[address] = (entry.Original, entry.Existed, state);
        }

        long fees = 0;
        for (var i = 1; i < block.Transactions.Count; i++)
        {
            var tx = block.Transactions[i];
            if (tx.ExpiresAtHeight != 0 && height > tx.ExpiresAtHeight)
                throw new ConsensusException($"La transacción {tx.Id.ToShortString()} expiró.");
            if (!tx.VerifySignature())
                throw new ConsensusException($"Firma inválida en la transacción {tx.Id.ToShortString()}.");
            var sender = tx.Sender;
            if (sender.Equals(tx.Recipient))
                throw new ConsensusException("No se permiten transferencias a la misma dirección.");

            var from = Get(sender);
            if (tx.Nonce != from.Nonce)
                throw new ConsensusException($"Nonce inválido: se esperaba {from.Nonce} y se recibió {tx.Nonce}.");
            long cost;
            try
            {
                cost = checked(tx.Amount + tx.Fee);
            }
            catch (OverflowException)
            {
                throw new ConsensusException("Desbordamiento en importe + comisión.");
            }
            if (from.Balance < cost)
                throw new ConsensusException("Saldo insuficiente: el bloque intenta gastar monedas inexistentes.");
            Set(sender, new AccountState(from.Balance - cost, from.Nonce + 1));

            var to = Get(tx.Recipient);
            Set(tx.Recipient, new AccountState(checked(to.Balance + tx.Amount), to.Nonce));
            fees = checked(fees + tx.Fee);
        }

        var coinbase = block.Transactions[0];
        if (coinbase.Height != height)
            throw new ConsensusException("La coinbase declara una altura distinta.");
        var subsidy = Emission.SubsidyAt(Network, height);
        if (issued + subsidy > Network.MaxSupply)
            throw new ConsensusException("La emisión superaría el suministro máximo.");
        if (coinbase.Amount != subsidy + fees)
            throw new ConsensusException("La recompensa de la coinbase no coincide con subsidio + comisiones.");
        var minerAccount = Get(coinbase.Recipient);
        Set(coinbase.Recipient, new AccountState(checked(minerAccount.Balance + coinbase.Amount), minerAccount.Nonce));

        var undo = new ShentWriter(64 + touched.Count * 40);
        undo.WriteI64(issued).WriteVarInt((ulong)touched.Count);
        foreach (var (address, entry) in touched)
        {
            undo.WriteBytes(address.Hash)
                .WriteU8(entry.Existed ? (byte)1 : (byte)0)
                .WriteI64(entry.Original.Balance)
                .WriteI64(entry.Original.Nonce);

            if (!entry.Existed && entry.Current == AccountState.Empty) continue;
            _store.PutAccount(address, entry.Current);
        }

        issued += subsidy;
        _store.PutUndo(idx.Hash, undo.ToArray());
        _store.SetMainChain(height, idx.Hash);
        for (var pos = 0; pos < block.Transactions.Count; pos++)
        {
            var tx = block.Transactions[pos];
            IEnumerable<Address> addresses = tx.IsCoinbase ? [tx.Recipient] : [tx.Sender, tx.Recipient];
            _store.IndexTransaction(tx.Id, idx.Hash, height, pos, addresses);
        }
        _store.SetBlockStatus(idx.Hash, BlockStatus.Valid);
    }

    private void DisconnectBlock(BlockIndex idx, ref long issued)
    {
        var undoBytes = _store.GetUndo(idx.Hash)
            ?? throw new InvalidOperationException($"Faltan los datos para deshacer el bloque {idx.Height}.");
        var r = new ShentReader(undoBytes);
        var issuedBefore = r.ReadI64();
        var count = r.ReadCount(1_000_000);
        for (var i = 0; i < count; i++)
        {
            var address = Address.FromHash(r.ReadBytes(Address.HashSize));
            var existed = r.ReadU8() == 1;
            var state = new AccountState(r.ReadI64(), r.ReadI64());
            if (existed) _store.PutAccount(address, state);
            else _store.DeleteAccount(address);
        }
        issued = issuedBefore;
        _store.RemoveIndexesAtHeight(idx.Height);
        _store.RemoveMainChain(idx.Height);
        _store.DeleteUndo(idx.Hash);
    }

    // =====================================================================
    // Dificultad y tiempo
    // =====================================================================

    private uint NextBitsLocked(BlockIndex parent)
    {
        if (Network.NoRetargeting) return Network.InitialBits;
        var window = new List<DifficultyCalculator.BlockTiming>(Network.DifficultyWindow + 1);
        for (var cur = parent; cur is not null && window.Count < Network.DifficultyWindow + 1; cur = cur.Prev)
        {
            window.Add(new DifficultyCalculator.BlockTiming(cur.Timestamp, cur.Bits));
        }
        window.Reverse();
        return DifficultyCalculator.NextBits(Network, parent.Height, window);
    }

    private long MedianTimePastLocked(BlockIndex index)
    {
        var times = new List<long>(Network.MedianTimeSpan);
        for (var cur = index; cur is not null && times.Count < Network.MedianTimeSpan; cur = cur.Prev)
        {
            times.Add(cur.Timestamp);
        }
        times.Sort();
        return times[times.Count / 2];
    }

    public TemplateContext GetTemplateContext()
    {
        lock (_gate)
        {
            return new TemplateContext(_tip, NextBitsLocked(_tip), MedianTimePastLocked(_tip), _issued);
        }
    }

    /// <summary>Hashrate estimado de la red a partir del trabajo y el tiempo de los últimos bloques.</summary>
    public double EstimateNetworkHashrate(int blocks = 60)
    {
        lock (_gate)
        {
            var tip = _tip;
            var start = tip;
            for (var i = 0; i < blocks && start.Prev is not null; i++) start = start.Prev;
            var span = tip.Timestamp - start.Timestamp;
            if (span <= 0 || tip.Height == start.Height) return 0;
            return (double)(tip.TotalWork - start.TotalWork) / span;
        }
    }

    // =====================================================================
    // Consultas
    // =====================================================================

    private Block LoadBlockLocked(Hash256 hash)
    {
        var data = _store.GetBlockData(hash) ?? throw new InvalidOperationException($"Bloque {hash} no encontrado.");
        return Block.Deserialize(data, int.MaxValue);
    }

    public Block? GetBlock(Hash256 hash)
    {
        lock (_gate)
        {
            var data = _store.GetBlockData(hash);
            return data is null ? null : Block.Deserialize(data, int.MaxValue);
        }
    }

    public Block? GetBlockAtHeight(long height)
    {
        lock (_gate)
        {
            var hash = _store.GetMainChainHash(height);
            return hash is null ? null : LoadBlockLocked(hash.Value);
        }
    }

    public bool IsInMainChain(Hash256 hash)
    {
        lock (_gate)
        {
            return _index.TryGetValue(hash, out var idx) && _store.GetMainChainHash(idx.Height) == hash;
        }
    }

    public AccountState GetAccount(Address address)
    {
        lock (_gate) return _store.GetAccount(address, out _);
    }

    /// <summary>Localizador de bloques: hashes de la cadena principal densos al principio y espaciados exponencialmente.</summary>
    public List<Hash256> GetLocator()
    {
        lock (_gate)
        {
            var heights = new List<long>();
            long step = 1;
            for (var h = _tip.Height; h > 0; h -= step)
            {
                heights.Add(h);
                if (heights.Count >= 10) step *= 2;
            }
            heights.Add(0);

            var result = new List<Hash256>(heights.Count);
            var cur = _tip;
            foreach (var h in heights)
            {
                cur = cur.GetAncestor(h)!;
                result.Add(cur.Hash);
            }
            return result;
        }
    }

    /// <summary>Respuesta a GET_BLOCKS: hashes de la cadena principal posteriores al primer punto común del localizador.</summary>
    public List<Hash256> GetHashesAfterLocator(IReadOnlyList<Hash256> locator, Hash256 stop, int max)
    {
        lock (_gate)
        {
            long start = 1;
            foreach (var hash in locator)
            {
                if (_index.TryGetValue(hash, out var idx) && _store.GetMainChainHash(idx.Height) == hash)
                {
                    start = idx.Height + 1;
                    break;
                }
            }
            var hashes = _store.GetMainChainRange(start, max);
            if (!stop.IsZero)
            {
                var stopAt = hashes.IndexOf(stop);
                if (stopAt >= 0) hashes = hashes.Take(stopAt + 1).ToList();
            }
            return hashes;
        }
    }

    public List<BlockSummary> GetRecentBlocks(int count, long? fromHeight = null)
    {
        lock (_gate)
        {
            var result = new List<BlockSummary>(count);
            var start = fromHeight is null ? _tip : _tip.GetAncestor(Math.Min(fromHeight.Value, _tip.Height));
            for (var cur = start; cur is not null && result.Count < count; cur = cur.Prev)
            {
                result.Add(Summarize(cur, LoadBlockLocked(cur.Hash)));
            }
            return result;
        }
    }

    private BlockSummary Summarize(BlockIndex idx, Block block)
    {
        var fees = block.Transactions.Skip(1).Sum(t => t.Fee);
        return new BlockSummary(idx.Height, idx.Hash, idx.Timestamp, block.Transactions.Count, idx.Bits,
            block.Coinbase.Recipient, block.Coinbase.Amount, fees, block.Serialize().Length);
    }

    public TxLocation? GetTransaction(Hash256 txid)
    {
        lock (_gate)
        {
            var location = _store.FindTransaction(txid);
            if (location is null) return null;
            var (blockHash, height, position) = location.Value;
            var block = LoadBlockLocked(blockHash);
            return new TxLocation(block.Transactions[position], height, blockHash, block.Header.Timestamp, position, _tip.Height - height + 1);
        }
    }

    public List<TxLocation> GetAddressHistory(Address address, int limit = 100)
    {
        lock (_gate)
        {
            var result = new List<TxLocation>();
            var cache = new Dictionary<long, Block>();
            foreach (var (height, position) in _store.GetAddressHistory(address, limit))
            {
                if (!cache.TryGetValue(height, out var block))
                {
                    var hash = _store.GetMainChainHash(height);
                    if (hash is null) continue;
                    block = LoadBlockLocked(hash.Value);
                    cache[height] = block;
                }
                result.Add(new TxLocation(block.Transactions[position], height, block.Hash, block.Header.Timestamp, position, _tip.Height - height + 1));
            }
            return result;
        }
    }

    /// <summary>
    /// Conciliación: la suma de todos los saldos debe ser exactamente igual al suministro emitido
    /// (las comisiones solo se redistribuyen al minero, nunca se crean ni se destruyen monedas).
    /// </summary>
    public (bool Consistent, long SumOfBalances, long Issued, long Accounts) Reconcile()
    {
        lock (_gate)
        {
            var (sum, count) = _store.SumAccounts();
            var expected = Emission.CumulativeSupply(Network, _tip.Height);
            return (sum == _issued && _issued == expected, sum, _issued, count);
        }
    }

    public void Dispose()
    {
        lock (_gate) _store.Dispose();
    }
}
