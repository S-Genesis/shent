using System.Diagnostics;
using System.Security.Cryptography;
using Shent.Core.Chain;
using Shent.Core.Consensus;
using Shent.Core.Crypto;
using Shent.Core.Model;

namespace Shent.Core.Mining;

public sealed record MinedBlockInfo(long Height, Hash256 Hash, long Reward, DateTimeOffset FoundAt);

/// <summary>
/// Minero de CPU multihilo. Los hilos corren con prioridad baja y la "intensidad" introduce pausas
/// proporcionales para que la computadora siga respondiendo con normalidad mientras mina.
/// </summary>
public sealed class Miner : IDisposable
{
    private const int HistorySize = 120;
    private const int TemplateRefreshSeconds = 15;

    private readonly Func<Address, BlockTemplate> _templateFactory;
    private readonly Func<Block, BlockAcceptResult> _submit;
    private readonly PowHasher _pow;
    private readonly object _gate = new();
    private readonly Queue<double> _history = new();

    private BlockTemplate? _template;
    private int _templateVersion;
    private Thread[] _threads = [];
    private CancellationTokenSource? _cts;
    private Timer? _statsTimer;
    private long _totalHashes;
    private long _lastHashes;
    private long _lastStatsTicks;
    private volatile int _intensity = 100;
    private double _hashrate;
    private long _blocksFound;
    private DateTimeOffset _templateCreated;

    public Address? Payout { get; private set; }
    public int ThreadCount { get; private set; }
    public bool IsRunning => _cts is not null;
    public double Hashrate => Volatile.Read(ref _hashrate);
    public long TotalHashes => Interlocked.Read(ref _totalHashes);
    public long BlocksFound => Interlocked.Read(ref _blocksFound);
    public DateTimeOffset? StartedAt { get; private set; }
    public long? CurrentHeight => _template?.Height;

    public int Intensity
    {
        get => _intensity;
        set => _intensity = Math.Clamp(value, 10, 100);
    }

    public event Action<MinedBlockInfo>? BlockFound;
    public event Action<string>? Log;

    public Miner(PowHasher pow, Func<Address, BlockTemplate> templateFactory, Func<Block, BlockAcceptResult> submit)
    {
        _pow = pow;
        _templateFactory = templateFactory;
        _submit = submit;
    }

    public IReadOnlyList<double> HashrateHistory
    {
        get { lock (_history) return _history.ToArray(); }
    }

    public void Start(Address payout, int threads, int intensity)
    {
        lock (_gate)
        {
            if (IsRunning) StopLocked();
            Payout = payout;
            ThreadCount = Math.Clamp(threads, 1, Environment.ProcessorCount);
            Intensity = intensity;
            _cts = new CancellationTokenSource();
            StartedAt = DateTimeOffset.UtcNow;
            RefreshTemplate();

            var token = _cts.Token;
            _threads = new Thread[ThreadCount];
            for (var i = 0; i < ThreadCount; i++)
            {
                var thread = new Thread(() => Work(token))
                {
                    IsBackground = true,
                    Priority = ThreadPriority.BelowNormal,
                    Name = $"shent-miner-{i}",
                };
                _threads[i] = thread;
                thread.Start();
            }
            _lastHashes = TotalHashes;
            _lastStatsTicks = Stopwatch.GetTimestamp();
            _statsTimer = new Timer(_ => SampleStats(), null, 1000, 1000);
            Log?.Invoke($"Minería iniciada con {ThreadCount} hilo(s) al {Intensity}% de intensidad.");
        }
    }

    public void Stop()
    {
        lock (_gate) StopLocked();
    }

    private void StopLocked()
    {
        if (_cts is null) return;
        _cts.Cancel();
        foreach (var t in _threads) t.Join(TimeSpan.FromSeconds(5));
        _cts.Dispose();
        _cts = null;
        _threads = [];
        _statsTimer?.Dispose();
        _statsTimer = null;
        Volatile.Write(ref _hashrate, 0);
        StartedAt = null;
        Log?.Invoke("Minería detenida.");
    }

    /// <summary>Recrea la plantilla (nueva punta de cadena, nuevas transacciones o marca de tiempo vieja).</summary>
    public void RefreshTemplate()
    {
        var payout = Payout;
        if (payout is null || _cts is null) return;
        try
        {
            var template = _templateFactory(payout);
            Volatile.Write(ref _template, template);
            _templateCreated = DateTimeOffset.UtcNow;
            Interlocked.Increment(ref _templateVersion);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"No se pudo preparar el bloque candidato: {ex.Message}");
        }
    }

    private void Work(CancellationToken token)
    {
        Span<byte> hash = stackalloc byte[32];
        Span<byte> nonceSeed = stackalloc byte[8];
        while (!token.IsCancellationRequested)
        {
            var template = Volatile.Read(ref _template);
            if (template is null)
            {
                Thread.Sleep(200);
                continue;
            }
            var version = Volatile.Read(ref _templateVersion);
            var header = (byte[])template.HeaderBytes.Clone();
            RandomNumberGenerator.Fill(nonceSeed);
            var nonce = BitConverter.ToUInt64(nonceSeed);
            var batch = 0;
            var batchStart = Stopwatch.GetTimestamp();

            while (!token.IsCancellationRequested && version == Volatile.Read(ref _templateVersion))
            {
                BlockHeader.WriteNonce(header, nonce);
                _pow.Hash(header, hash);
                batch++;

                if (PowHasher.MeetsTarget(hash, template.Target))
                {
                    Interlocked.Add(ref _totalHashes, batch);
                    batch = 0;
                    OnSolution(template, nonce);
                    break;
                }
                nonce++;

                if (batch >= 16)
                {
                    Interlocked.Add(ref _totalHashes, batch);
                    batch = 0;
                    var intensity = _intensity;
                    if (intensity < 100)
                    {
                        var elapsed = Stopwatch.GetElapsedTime(batchStart);
                        var pause = elapsed * (100 - intensity) / intensity;
                        if (pause > TimeSpan.Zero) Thread.Sleep(pause);
                    }
                    batchStart = Stopwatch.GetTimestamp();
                    if ((DateTimeOffset.UtcNow - _templateCreated).TotalSeconds > TemplateRefreshSeconds
                        && Monitor.TryEnter(_gate))
                    {
                        try { RefreshTemplate(); }
                        finally { Monitor.Exit(_gate); }
                    }
                }
            }
            if (batch > 0) Interlocked.Add(ref _totalHashes, batch);
        }
    }

    private void OnSolution(BlockTemplate template, ulong nonce)
    {
        var block = new Block
        {
            Header = template.Block.Header.WithNonce(nonce),
            Transactions = template.Block.Transactions,
        };
        var result = _submit(block);
        if (result.Status == BlockAcceptStatus.NewTip)
        {
            Interlocked.Increment(ref _blocksFound);
            var info = new MinedBlockInfo(template.Height, block.Hash, template.Reward, DateTimeOffset.UtcNow);
            Log?.Invoke($"¡Bloque {template.Height} encontrado! Recompensa: {Money.Format(template.Reward)} SHENT.");
            BlockFound?.Invoke(info);
        }
        else
        {
            Log?.Invoke($"Solución descartada para el bloque {template.Height}: {result.Reason ?? result.Status.ToString()}");
        }
        RefreshTemplate();
    }

    private void SampleStats()
    {
        var now = Stopwatch.GetTimestamp();
        var hashes = TotalHashes;
        var seconds = Stopwatch.GetElapsedTime(_lastStatsTicks, now).TotalSeconds;
        if (seconds <= 0) return;
        var instant = (hashes - _lastHashes) / seconds;
        _lastHashes = hashes;
        _lastStatsTicks = now;
        var previous = Volatile.Read(ref _hashrate);
        var smoothed = previous <= 0 ? instant : previous * 0.7 + instant * 0.3;
        Volatile.Write(ref _hashrate, smoothed);
        lock (_history)
        {
            _history.Enqueue(smoothed);
            while (_history.Count > HistorySize) _history.Dequeue();
        }
    }

    public void Dispose() => Stop();
}
