using System.Numerics;
using Microsoft.Data.Sqlite;
using Shent.Core.Consensus;
using Shent.Core.Crypto;

namespace Shent.Core.Chain;

public enum BlockStatus
{
    /// <summary>Cabecera y prueba de trabajo verificadas; el contenido aún no se aplicó al estado.</summary>
    Stored = 1,
    /// <summary>Bloque aplicado al estado al menos una vez: todas las reglas de consenso verificadas.</summary>
    Valid = 2,
    Invalid = 3,
}

public readonly record struct AccountState(long Balance, long Nonce)
{
    public static readonly AccountState Empty = new(0, 0);
}

public sealed record StoredBlockIndex(
    Hash256 Hash, long Height, Hash256 PrevHash, long Timestamp, uint Bits, BigInteger TotalWork, BlockStatus Status);

/// <summary>
/// Persistencia SQLite de un nodo. No es seguro para uso concurrente: <see cref="Blockchain"/> serializa el acceso.
/// </summary>
public sealed class ChainStore : IDisposable
{
    private const int SchemaVersion = 1;
    private readonly SqliteConnection _db;
    private SqliteTransaction? _tx;

    public ChainStore(string path)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        };
        _db = new SqliteConnection(builder.ToString());
        _db.Open();
        Execute("PRAGMA journal_mode=WAL;");
        Execute("PRAGMA synchronous=FULL;");
        Execute("PRAGMA foreign_keys=ON;");
        CreateSchema();
    }

    private void CreateSchema()
    {
        Execute("""
            CREATE TABLE IF NOT EXISTS meta (
                key   TEXT PRIMARY KEY,
                value BLOB NOT NULL
            );
            CREATE TABLE IF NOT EXISTS blocks (
                hash        BLOB PRIMARY KEY,
                height      INTEGER NOT NULL,
                prev_hash   BLOB NOT NULL,
                timestamp   INTEGER NOT NULL,
                bits        INTEGER NOT NULL,
                total_work  BLOB NOT NULL,
                status      INTEGER NOT NULL,
                data        BLOB NOT NULL,
                received_at INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_blocks_height ON blocks(height);
            CREATE TABLE IF NOT EXISTS main_chain (
                height INTEGER PRIMARY KEY,
                hash   BLOB NOT NULL UNIQUE
            );
            CREATE TABLE IF NOT EXISTS accounts (
                address BLOB PRIMARY KEY,
                balance INTEGER NOT NULL CHECK (balance >= 0),
                nonce   INTEGER NOT NULL CHECK (nonce >= 0)
            );
            CREATE TABLE IF NOT EXISTS undo (
                block_hash BLOB PRIMARY KEY,
                data       BLOB NOT NULL
            );
            CREATE TABLE IF NOT EXISTS tx_index (
                txid       BLOB PRIMARY KEY,
                block_hash BLOB NOT NULL,
                height     INTEGER NOT NULL,
                position   INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_tx_index_height ON tx_index(height);
            CREATE TABLE IF NOT EXISTS address_tx (
                address  BLOB NOT NULL,
                height   INTEGER NOT NULL,
                position INTEGER NOT NULL,
                txid     BLOB NOT NULL,
                PRIMARY KEY (address, height, position)
            );
            CREATE INDEX IF NOT EXISTS ix_address_tx_height ON address_tx(height);
            """);
        var version = GetMetaLong("schema_version");
        if (version is null)
        {
            SetMetaLong("schema_version", SchemaVersion);
        }
        else if (version != SchemaVersion)
        {
            throw new InvalidOperationException($"Versión de base de datos no soportada: {version}.");
        }
    }

    private SqliteCommand Command(string sql)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = _tx;
        return cmd;
    }

    private void Execute(string sql)
    {
        using var cmd = Command(sql);
        cmd.ExecuteNonQuery();
    }

    public void BeginTransaction()
    {
        if (_tx is not null) throw new InvalidOperationException("Ya hay una transacción abierta.");
        _tx = _db.BeginTransaction();
    }

    public void Commit()
    {
        _tx?.Commit();
        _tx?.Dispose();
        _tx = null;
    }

    public void Rollback()
    {
        _tx?.Rollback();
        _tx?.Dispose();
        _tx = null;
    }

    // ---------- meta ----------

    public byte[]? GetMeta(string key)
    {
        using var cmd = Command("SELECT value FROM meta WHERE key = $k");
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as byte[];
    }

    public void SetMeta(string key, byte[] value)
    {
        using var cmd = Command("INSERT INTO meta(key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value");
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    public long? GetMetaLong(string key)
    {
        var value = GetMeta(key);
        return value is null ? null : BitConverter.ToInt64(value);
    }

    public void SetMetaLong(string key, long value) => SetMeta(key, BitConverter.GetBytes(value));

    // ---------- bloques ----------

    public bool HasBlock(Hash256 hash)
    {
        using var cmd = Command("SELECT 1 FROM blocks WHERE hash = $h");
        cmd.Parameters.AddWithValue("$h", hash.ToArray());
        return cmd.ExecuteScalar() is not null;
    }

    public void PutBlock(Hash256 hash, long height, Hash256 prev, long timestamp, uint bits, BigInteger totalWork, BlockStatus status, byte[] data)
    {
        using var cmd = Command("""
            INSERT OR IGNORE INTO blocks(hash, height, prev_hash, timestamp, bits, total_work, status, data, received_at)
            VALUES ($h, $height, $prev, $ts, $bits, $work, $status, $data, $now)
            """);
        cmd.Parameters.AddWithValue("$h", hash.ToArray());
        cmd.Parameters.AddWithValue("$height", height);
        cmd.Parameters.AddWithValue("$prev", prev.ToArray());
        cmd.Parameters.AddWithValue("$ts", timestamp);
        cmd.Parameters.AddWithValue("$bits", (long)bits);
        cmd.Parameters.AddWithValue("$work", totalWork.ToByteArray(isUnsigned: true, isBigEndian: true));
        cmd.Parameters.AddWithValue("$status", (int)status);
        cmd.Parameters.AddWithValue("$data", data);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.ExecuteNonQuery();
    }

    public void SetBlockStatus(Hash256 hash, BlockStatus status)
    {
        using var cmd = Command("UPDATE blocks SET status = $s WHERE hash = $h");
        cmd.Parameters.AddWithValue("$s", (int)status);
        cmd.Parameters.AddWithValue("$h", hash.ToArray());
        cmd.ExecuteNonQuery();
    }

    public byte[]? GetBlockData(Hash256 hash)
    {
        using var cmd = Command("SELECT data FROM blocks WHERE hash = $h");
        cmd.Parameters.AddWithValue("$h", hash.ToArray());
        return cmd.ExecuteScalar() as byte[];
    }

    public List<StoredBlockIndex> LoadIndex()
    {
        var result = new List<StoredBlockIndex>();
        using var cmd = Command("SELECT hash, height, prev_hash, timestamp, bits, total_work, status FROM blocks ORDER BY height ASC");
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new StoredBlockIndex(
                new Hash256((byte[])reader[0]),
                reader.GetInt64(1),
                new Hash256((byte[])reader[2]),
                reader.GetInt64(3),
                (uint)reader.GetInt64(4),
                new BigInteger((byte[])reader[5], isUnsigned: true, isBigEndian: true),
                (BlockStatus)reader.GetInt32(6)));
        }
        return result;
    }

    // ---------- cadena principal ----------

    public void SetMainChain(long height, Hash256 hash)
    {
        using var cmd = Command("INSERT INTO main_chain(height, hash) VALUES ($h, $hash)");
        cmd.Parameters.AddWithValue("$h", height);
        cmd.Parameters.AddWithValue("$hash", hash.ToArray());
        cmd.ExecuteNonQuery();
    }

    public void RemoveMainChain(long height)
    {
        using var cmd = Command("DELETE FROM main_chain WHERE height = $h");
        cmd.Parameters.AddWithValue("$h", height);
        cmd.ExecuteNonQuery();
    }

    public Hash256? GetMainChainHash(long height)
    {
        using var cmd = Command("SELECT hash FROM main_chain WHERE height = $h");
        cmd.Parameters.AddWithValue("$h", height);
        return cmd.ExecuteScalar() is byte[] bytes ? new Hash256(bytes) : null;
    }

    public List<Hash256> GetMainChainRange(long fromHeight, int count)
    {
        var result = new List<Hash256>(count);
        using var cmd = Command("SELECT hash FROM main_chain WHERE height >= $from ORDER BY height ASC LIMIT $n");
        cmd.Parameters.AddWithValue("$from", fromHeight);
        cmd.Parameters.AddWithValue("$n", count);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new Hash256((byte[])reader[0]));
        }
        return result;
    }

    // ---------- cuentas ----------

    public AccountState GetAccount(Address address, out bool exists)
    {
        using var cmd = Command("SELECT balance, nonce FROM accounts WHERE address = $a");
        cmd.Parameters.AddWithValue("$a", address.Hash.ToArray());
        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            exists = true;
            return new AccountState(reader.GetInt64(0), reader.GetInt64(1));
        }
        exists = false;
        return AccountState.Empty;
    }

    public void PutAccount(Address address, AccountState state)
    {
        using var cmd = Command("""
            INSERT INTO accounts(address, balance, nonce) VALUES ($a, $b, $n)
            ON CONFLICT(address) DO UPDATE SET balance = excluded.balance, nonce = excluded.nonce
            """);
        cmd.Parameters.AddWithValue("$a", address.Hash.ToArray());
        cmd.Parameters.AddWithValue("$b", state.Balance);
        cmd.Parameters.AddWithValue("$n", state.Nonce);
        cmd.ExecuteNonQuery();
    }

    public void DeleteAccount(Address address)
    {
        using var cmd = Command("DELETE FROM accounts WHERE address = $a");
        cmd.Parameters.AddWithValue("$a", address.Hash.ToArray());
        cmd.ExecuteNonQuery();
    }

    public (long Sum, long Count) SumAccounts()
    {
        using var cmd = Command("SELECT COALESCE(SUM(balance), 0), COUNT(*) FROM accounts");
        using var reader = cmd.ExecuteReader();
        reader.Read();
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    // ---------- deshacer ----------

    public void PutUndo(Hash256 blockHash, byte[] data)
    {
        using var cmd = Command("INSERT OR REPLACE INTO undo(block_hash, data) VALUES ($h, $d)");
        cmd.Parameters.AddWithValue("$h", blockHash.ToArray());
        cmd.Parameters.AddWithValue("$d", data);
        cmd.ExecuteNonQuery();
    }

    public byte[]? GetUndo(Hash256 blockHash)
    {
        using var cmd = Command("SELECT data FROM undo WHERE block_hash = $h");
        cmd.Parameters.AddWithValue("$h", blockHash.ToArray());
        return cmd.ExecuteScalar() as byte[];
    }

    public void DeleteUndo(Hash256 blockHash)
    {
        using var cmd = Command("DELETE FROM undo WHERE block_hash = $h");
        cmd.Parameters.AddWithValue("$h", blockHash.ToArray());
        cmd.ExecuteNonQuery();
    }

    // ---------- índices de transacciones ----------

    public void IndexTransaction(Hash256 txid, Hash256 blockHash, long height, int position, IEnumerable<Address> addresses)
    {
        using (var cmd = Command("INSERT OR REPLACE INTO tx_index(txid, block_hash, height, position) VALUES ($t, $b, $h, $p)"))
        {
            cmd.Parameters.AddWithValue("$t", txid.ToArray());
            cmd.Parameters.AddWithValue("$b", blockHash.ToArray());
            cmd.Parameters.AddWithValue("$h", height);
            cmd.Parameters.AddWithValue("$p", position);
            cmd.ExecuteNonQuery();
        }
        foreach (var address in addresses.Distinct())
        {
            using var cmd = Command("INSERT OR IGNORE INTO address_tx(address, height, position, txid) VALUES ($a, $h, $p, $t)");
            cmd.Parameters.AddWithValue("$a", address.Hash.ToArray());
            cmd.Parameters.AddWithValue("$h", height);
            cmd.Parameters.AddWithValue("$p", position);
            cmd.Parameters.AddWithValue("$t", txid.ToArray());
            cmd.ExecuteNonQuery();
        }
    }

    public void RemoveIndexesAtHeight(long height)
    {
        using (var cmd = Command("DELETE FROM tx_index WHERE height = $h"))
        {
            cmd.Parameters.AddWithValue("$h", height);
            cmd.ExecuteNonQuery();
        }
        using (var cmd = Command("DELETE FROM address_tx WHERE height = $h"))
        {
            cmd.Parameters.AddWithValue("$h", height);
            cmd.ExecuteNonQuery();
        }
    }

    public (Hash256 BlockHash, long Height, int Position)? FindTransaction(Hash256 txid)
    {
        using var cmd = Command("SELECT block_hash, height, position FROM tx_index WHERE txid = $t");
        cmd.Parameters.AddWithValue("$t", txid.ToArray());
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return (new Hash256((byte[])reader[0]), reader.GetInt64(1), reader.GetInt32(2));
    }

    public List<(long Height, int Position)> GetAddressHistory(Address address, int limit)
    {
        var result = new List<(long, int)>();
        using var cmd = Command("SELECT height, position FROM address_tx WHERE address = $a ORDER BY height DESC, position DESC LIMIT $n");
        cmd.Parameters.AddWithValue("$a", address.Hash.ToArray());
        cmd.Parameters.AddWithValue("$n", limit);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add((reader.GetInt64(0), reader.GetInt32(1)));
        }
        return result;
    }

    public void Dispose()
    {
        _tx?.Dispose();
        _db.Dispose();
    }
}
