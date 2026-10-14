using System.Text.Json;

namespace Shent.Core.Wallet;

/// <summary>
/// Espera creciente tras intentos fallidos de contraseña, aplicada solo en este dispositivo.
/// Después de 5 fallos: 5 s, 10 s, 20 s… hasta 15 minutos. Nunca bloquea la billetera en la red:
/// un tercero no puede impedir que el dueño use su billetera. La protección real contra ataques
/// fuera de línea es el costo de Argon2id.
/// </summary>
public sealed class UnlockThrottle
{
    public const int FreeAttempts = 5;
    private static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(15);

    private sealed class State
    {
        public int Failures { get; set; }
        public DateTimeOffset? NextAllowed { get; set; }
    }

    private readonly object _gate = new();
    private readonly string? _path;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Dictionary<string, State> _states;

    public UnlockThrottle(string? path, Func<DateTimeOffset>? clock = null)
    {
        _path = path;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _states = Load();
    }

    public TimeSpan RemainingWait(string walletKey)
    {
        lock (_gate)
        {
            if (!_states.TryGetValue(walletKey, out var s) || s.NextAllowed is null) return TimeSpan.Zero;
            var remaining = s.NextAllowed.Value - _clock();
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    public int Failures(string walletKey)
    {
        lock (_gate) return _states.TryGetValue(walletKey, out var s) ? s.Failures : 0;
    }

    public void RecordFailure(string walletKey)
    {
        lock (_gate)
        {
            if (!_states.TryGetValue(walletKey, out var s))
            {
                s = new State();
                _states[walletKey] = s;
            }
            s.Failures++;
            if (s.Failures >= FreeAttempts)
            {
                var exponent = Math.Min(s.Failures - FreeAttempts, 10);
                var delay = TimeSpan.FromTicks(Math.Min(MaxDelay.Ticks, BaseDelay.Ticks * (1L << exponent)));
                s.NextAllowed = _clock() + delay;
            }
            Save();
        }
    }

    public void RecordSuccess(string walletKey)
    {
        lock (_gate)
        {
            if (_states.Remove(walletKey)) Save();
        }
    }

    private Dictionary<string, State> Load()
    {
        if (_path is null || !File.Exists(_path)) return new Dictionary<string, State>();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, State>>(File.ReadAllText(_path)) ?? new Dictionary<string, State>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, State>();
        }
    }

    private void Save()
    {
        if (_path is null) return;
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(_states));
        }
        catch (IOException)
        {
            // Si no se puede persistir, la espera sigue vigente en memoria.
        }
    }
}
