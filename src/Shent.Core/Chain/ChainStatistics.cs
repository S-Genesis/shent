using Shent.Core.Consensus;
using Shent.Core.Crypto;

namespace Shent.Core.Chain;

/// <summary>Totales de un día UTC de la cadena principal.</summary>
public sealed record StatsDay(DateOnly Day, int Blocks, int Transactions, double Hashrate, int Miners, long IssuedAtEnd);

public sealed record MinerShare(Address Miner, int Blocks, double Share);

/// <summary>
/// Estadísticas calculadas solo a partir de bloques validados. "Mineros" cuenta direcciones distintas que
/// cobraron una recompensa: una persona puede usar varias direcciones y varias personas pueden compartir una.
/// </summary>
public sealed record ChainStats(
    long Height,
    long Issued,
    long MaxSupply,
    long Subsidy,
    long NextHalvingHeight,
    int Blocks24h,
    int Transactions24h,
    int Miners24h,
    int Miners7d,
    int MinersTotal,
    double Hashrate24h,
    double AverageBlockSeconds24h,
    IReadOnlyList<StatsDay> Days,
    IReadOnlyList<MinerShare> TopMiners7d);

/// <summary>
/// Agrega la cadena principal de forma incremental: solo lee los bloques nuevos y, ante una reorganización,
/// descuenta los bloques reemplazados. Guarda totales por día y el detalle de los últimos 7 días.
/// </summary>
public sealed class ChainStatistics
{
    private const int Day = 86_400;
    private const int Batch = 500;
    private const int ChartDays = 30;

    private sealed class DayBucket
    {
        public int Blocks;
        public int Transactions;
        public double Work;
        public long LastHeight;
        public readonly Dictionary<Address, int> Miners = new();
    }

    private readonly Blockchain _chain;
    private readonly SortedDictionary<long, DayBucket> _days = new();
    private readonly List<BlockSummary> _recent = new();
    private readonly Dictionary<Address, int> _allMiners = new();
    private readonly object _gate = new();
    private long _height = -1;
    private Hash256 _tipHash;

    public ChainStatistics(Blockchain chain) => _chain = chain;

    public ChainStats Snapshot(long nowUnix)
    {
        lock (_gate)
        {
            Update(nowUnix);
            return Build(nowUnix);
        }
    }

    private void Update(long nowUnix)
    {
        var tip = _chain.Tip;
        if (tip.Height == _height && tip.Hash == _tipHash) return;

        // Reorganización: retroceder hasta el último bloque que sigue en la cadena principal.
        while (_recent.Count > 0)
        {
            var last = _recent[^1];
            if (last.Height <= tip.Height && _chain.GetRecentBlocks(1, last.Height) is [{ } current] && current.Hash == last.Hash) break;
            Remove(last);
            _recent.RemoveAt(_recent.Count - 1);
        }
        if (_recent.Count == 0 && _height >= 0)
        {
            // Más profundo que el detalle guardado: se recalcula todo.
            _days.Clear();
            _allMiners.Clear();
        }
        var from = _recent.Count > 0 ? _recent[^1].Height + 1 : 0;

        for (var top = Math.Min(tip.Height, from + Batch - 1); from <= tip.Height; top = Math.Min(tip.Height, from + Batch - 1))
        {
            var blocks = _chain.GetRecentBlocks((int)(top - from + 1), top);
            blocks.Reverse();
            foreach (var b in blocks)
            {
                if (b.Height < from) continue;
                Add(b);
                _recent.Add(b);
            }
            if (blocks.Count == 0) break;
            from = blocks[^1].Height + 1;
        }

        // Detalle: últimos 7 días, y nunca menos que la reorganización máxima.
        var cutoff = nowUnix - 7 * Day;
        var keep = Math.Max(_chain.Network.MaxForkDepth + 1, 0);
        var removable = 0;
        while (removable < _recent.Count - keep && _recent[removable].Timestamp < cutoff) removable++;
        if (removable > 0) _recent.RemoveRange(0, removable);

        _height = tip.Height;
        _tipHash = tip.Hash;
    }

    // El génesis no se mina: no suma bloques, trabajo ni mineros.
    private void Add(BlockSummary b)
    {
        if (b.Height == 0) return;
        var bucket = Bucket(b.Timestamp, create: true)!;
        bucket.Blocks++;
        bucket.Transactions += Math.Max(0, b.TransactionCount - 1);
        bucket.Work += Target.Difficulty(b.Bits);
        bucket.LastHeight = Math.Max(bucket.LastHeight, b.Height);
        Increment(bucket.Miners, b.Miner, 1);
        Increment(_allMiners, b.Miner, 1);
    }

    private void Remove(BlockSummary b)
    {
        if (b.Height == 0) return;
        var key = b.Timestamp / Day;
        if (!_days.TryGetValue(key, out var bucket)) return;
        bucket.Blocks--;
        bucket.Transactions -= Math.Max(0, b.TransactionCount - 1);
        bucket.Work -= Target.Difficulty(b.Bits);
        Increment(bucket.Miners, b.Miner, -1);
        Increment(_allMiners, b.Miner, -1);
        if (bucket.Blocks <= 0) _days.Remove(key);
        else if (bucket.LastHeight == b.Height) bucket.LastHeight = b.Height - 1;
    }

    private DayBucket? Bucket(long timestamp, bool create)
    {
        var key = timestamp / Day;
        if (_days.TryGetValue(key, out var bucket) || !create) return bucket;
        return _days[key] = new DayBucket();
    }

    private static void Increment(Dictionary<Address, int> counts, Address address, int delta)
    {
        var value = counts.GetValueOrDefault(address) + delta;
        if (value <= 0) counts.Remove(address);
        else counts[address] = value;
    }

    private ChainStats Build(long nowUnix)
    {
        var network = _chain.Network;
        var last24 = _recent.Where(b => b.Timestamp > nowUnix - Day && b.Height > 0).ToList();
        var last7 = _recent.Where(b => b.Timestamp > nowUnix - 7 * Day && b.Height > 0).ToList();

        var firstBlockTime = _recent.FirstOrDefault(b => b.Height > 0)?.Timestamp ?? nowUnix;
        var window = Math.Clamp(nowUnix - Math.Max(firstBlockTime, nowUnix - Day), 1, Day);
        var hashrate = last24.Sum(b => Target.Difficulty(b.Bits)) / window;
        var avgSeconds = last24.Count >= 2 ? (double)(last24[^1].Timestamp - last24[0].Timestamp) / (last24.Count - 1) : 0;

        var top = last7.GroupBy(b => b.Miner)
            .Select(g => new MinerShare(g.Key, g.Count(), (double)g.Count() / last7.Count))
            .OrderByDescending(m => m.Blocks)
            .Take(5)
            .ToList();

        return new ChainStats(
            _height,
            _chain.IssuedSupply,
            network.MaxSupply,
            Emission.SubsidyAt(network, _height + 1),
            Emission.NextHalvingHeight(network, _height + 1),
            last24.Count,
            last24.Sum(b => Math.Max(0, b.TransactionCount - 1)),
            last24.Select(b => b.Miner).Distinct().Count(),
            last7.Select(b => b.Miner).Distinct().Count(),
            _allMiners.Count,
            hashrate,
            avgSeconds,
            DailySeries(nowUnix),
            top);
    }

    private List<StatsDay> DailySeries(long nowUnix)
    {
        var network = _chain.Network;
        var today = nowUnix / Day;
        var firstDay = _days.Count > 0 ? Math.Max(_days.Keys.First(), today - ChartDays + 1) : today;
        var series = new List<StatsDay>();
        long issued = 0;
        foreach (var (key, bucket) in _days)
        {
            if (key >= firstDay) break;
            issued = Emission.CumulativeSupply(network, bucket.LastHeight);
        }
        for (var key = firstDay; key <= today; key++)
        {
            var seconds = key == today ? Math.Max(1, nowUnix - key * Day) : Day;
            if (_days.TryGetValue(key, out var bucket))
            {
                issued = Emission.CumulativeSupply(network, bucket.LastHeight);
                series.Add(new StatsDay(DayOf(key), bucket.Blocks, bucket.Transactions, bucket.Work / seconds, bucket.Miners.Count, issued));
            }
            else
            {
                series.Add(new StatsDay(DayOf(key), 0, 0, 0, 0, issued));
            }
        }
        return series;
    }

    private static DateOnly DayOf(long dayNumber) => DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(dayNumber * Day).UtcDateTime);
}
