using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace Shent.Core.P2P;

public sealed class AddressEntry
{
    public required string Ip { get; set; }
    public required int Port { get; set; }
    /// <summary>Peer ID esperado en esta dirección (verificado si <see cref="LastSuccess"/> tiene valor).</summary>
    public string? PeerId { get; set; }
    /// <summary>Si no es null, la dirección es un circuito: Ip:Port es el relay y este es el nodo destino.</summary>
    public string? CircuitTarget { get; set; }
    public string Source { get; set; } = "peer";
    public DateTimeOffset FirstSeen { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastSuccess { get; set; }
    public DateTimeOffset? LastAttempt { get; set; }
    public int Failures { get; set; }
    public bool Manual { get; set; }

    public PeerAddress ToAddress() => new(IPAddress.Parse(Ip), Port);

    public NodeAddress ToNodeAddress()
    {
        P2P.PeerId.TryParse(PeerId, out var id);
        P2P.PeerId.TryParse(CircuitTarget, out var target);
        return new NodeAddress(ToAddress(), id, id is null ? null : target);
    }

    /// <summary>
    /// Espera tras fallos. Pares manuales y de arranque: 2 s, 4 s, 8 s… hasta 5 min (reconexión rápida).
    /// Resto: 30 s, 1 min, 2 min… hasta 1 h.
    /// </summary>
    public bool ReadyForAttempt(DateTimeOffset now)
    {
        if (LastAttempt is null) return true;
        if (Manual)
        {
            var wait = Failures == 0 ? 2 : Math.Min(300, 2 * Math.Pow(2, Math.Min(Failures - 1, 10)));
            return now - LastAttempt > TimeSpan.FromSeconds(wait);
        }
        if (Failures == 0) return now - LastAttempt > TimeSpan.FromSeconds(30);
        var backoff = TimeSpan.FromSeconds(Math.Min(3600, 30 * Math.Pow(2, Math.Min(Failures - 1, 7))));
        return now - LastAttempt > backoff;
    }
}

/// <summary>
/// Tabla local de pares conocidos, persistida en peers.json. Los nodos de arranque y semillas solo
/// alimentan esta tabla al principio: después la red se descubre a sí misma intercambiando direcciones,
/// y sigue funcionando aunque los nodos de arranque se apaguen.
/// </summary>
public sealed class AddressBook
{
    private const int MaxEntries = 5000;
    private const int MaxFailuresBeforeEviction = 12;
    private const string PeerBanPrefix = "peer:";

    private readonly object _gate = new();
    private readonly string? _path;
    private readonly Dictionary<string, AddressEntry> _entries = new();
    private readonly Dictionary<string, DateTimeOffset> _bans = new();

    public AddressBook(string? path)
    {
        _path = path;
        Load();
    }

    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    public static bool IsAcceptable(PeerAddress address, bool allowLoopback)
    {
        if (address.Port is <= 0 or > 65535) return false;
        var ip = address.Ip;
        if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.Broadcast)) return false;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6 && (ip.IsIPv6Multicast || ip.IsIPv6LinkLocal)) return false;
        if (ip.AddressFamily == AddressFamily.InterNetwork && ip.GetAddressBytes()[0] is >= 224 or 0) return false;
        if (IPAddress.IsLoopback(ip) && !allowLoopback) return false;
        return true;
    }

    public static bool IsPrivate(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6) return ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal || ip.IsIPv6LinkLocal;
        var b = ip.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] is >= 16 and <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254)
            || IsCarrierGradeNat(ip);
    }

    /// <summary>100.64.0.0/10: rango que usan los proveedores con CGNAT (IP compartida entre clientes).</summary>
    public static bool IsCarrierGradeNat(IPAddress ip)
    {
        ip = PeerAddress.Normalize(ip);
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 100 && b[1] is >= 64 and <= 127;
    }

    /// <summary>Grupo de red usado para diversificar conexiones salientes (/16 en IPv4, /32 en IPv6).</summary>
    public static string NetworkGroup(IPAddress ip)
    {
        if (IsPrivate(ip)) return "private:" + ip;
        var b = ip.GetAddressBytes();
        return ip.AddressFamily == AddressFamily.InterNetwork ? $"v4:{b[0]}.{b[1]}" : $"v6:{b[0]:x2}{b[1]:x2}{b[2]:x2}{b[3]:x2}";
    }

    private static AddressEntry NewEntry(NodeAddress address, string source, bool manual) => new()
    {
        Ip = address.Endpoint.Ip.ToString(),
        Port = address.Endpoint.Port,
        PeerId = address.PeerId?.ToString(),
        CircuitTarget = address.CircuitTarget?.ToString(),
        Source = source,
        Manual = manual,
    };

    public bool Add(NodeAddress address, string source, bool manual = false)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(address.Key, out var existing))
            {
                existing.Manual |= manual;
                // Una dirección escrita por el usuario fija el Peer ID esperado. Las aprendidas de otros pares
                // solo completan un ID desconocido: nunca reemplazan uno ya verificado.
                if (address.PeerId is not null && (manual || existing.PeerId is null && existing.LastSuccess is null))
                {
                    existing.PeerId = address.PeerId.ToString();
                }
                return false;
            }
            if (_entries.Count >= MaxEntries) EvictOne();
            _entries[address.Key] = NewEntry(address, source, manual);
            return true;
        }
    }

    public void Remove(NodeAddress address)
    {
        lock (_gate) _entries.Remove(address.Key);
    }

    public bool Contains(NodeAddress address)
    {
        lock (_gate) return _entries.ContainsKey(address.Key);
    }

    private void EvictOne()
    {
        var victim = _entries
            .Where(e => !e.Value.Manual)
            .OrderByDescending(e => e.Value.Failures)
            .ThenBy(e => e.Value.LastSuccess ?? DateTimeOffset.MinValue)
            .Select(e => e.Key)
            .FirstOrDefault();
        if (victim is not null) _entries.Remove(victim);
    }

    public void MarkAttempt(NodeAddress address)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(address.Key, out var e)) e.LastAttempt = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>Conexión exitosa con un par cuya identidad fue verificada criptográficamente en el handshake.</summary>
    public void MarkSuccess(NodeAddress address, PeerId? verifiedId = null)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(address.Key, out var e))
            {
                e = NewEntry(address, "verified", manual: false);
                _entries[address.Key] = e;
            }
            if (verifiedId is not null && !address.IsCircuit) e.PeerId = verifiedId.ToString();
            e.LastSuccess = DateTimeOffset.UtcNow;
            e.Failures = 0;
        }
    }

    public void MarkFailure(NodeAddress address)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(address.Key, out var e)) return;
            e.Failures++;
            if (e.Failures >= MaxFailuresBeforeEviction && !e.Manual) _entries.Remove(address.Key);
        }
    }

    public int GetFailures(NodeAddress address)
    {
        lock (_gate) return _entries.TryGetValue(address.Key, out var e) ? e.Failures : 0;
    }

    public void Ban(IPAddress ip, TimeSpan duration)
    {
        lock (_gate) _bans[PeerAddress.Normalize(ip).ToString()] = DateTimeOffset.UtcNow + duration;
    }

    public void Ban(PeerId id, TimeSpan duration)
    {
        lock (_gate) _bans[PeerBanPrefix + id] = DateTimeOffset.UtcNow + duration;
    }

    public bool IsBanned(IPAddress ip) => IsBannedKey(PeerAddress.Normalize(ip).ToString());

    public bool IsBanned(PeerId id) => IsBannedKey(PeerBanPrefix + id);

    private bool IsBannedKey(string key)
    {
        lock (_gate)
        {
            if (!_bans.TryGetValue(key, out var until)) return false;
            if (until > DateTimeOffset.UtcNow) return true;
            _bans.Remove(key);
            return false;
        }
    }

    public int BannedCount
    {
        get
        {
            lock (_gate) return _bans.Count(b => b.Value > DateTimeOffset.UtcNow);
        }
    }

    /// <summary>Candidatos para conexiones salientes: primero manuales y verificados, luego aleatorios.</summary>
    public List<NodeAddress> PickCandidates(ISet<string> exclude, int count)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            return _entries
                .Where(e => !exclude.Contains(e.Key) && e.Value.ReadyForAttempt(now) && !IsBannedUnlocked(e.Value))
                .OrderByDescending(e => e.Value.Manual)
                .ThenByDescending(e => e.Value.LastSuccess.HasValue)
                .ThenBy(e => e.Value.CircuitTarget is not null)
                .ThenBy(_ => RandomNumberGenerator.GetInt32(int.MaxValue))
                .Take(count)
                .Select(e => e.Value.ToNodeAddress())
                .ToList();
        }
    }

    private bool IsBannedUnlocked(AddressEntry e)
    {
        var now = DateTimeOffset.UtcNow;
        if (_bans.TryGetValue(e.Ip, out var until) && until > now && e.CircuitTarget is null) return true;
        var id = e.CircuitTarget ?? e.PeerId;
        return id is not null && _bans.TryGetValue(PeerBanPrefix + id, out var idUntil) && idUntil > now;
    }

    /// <summary>
    /// Direcciones para compartir con otros pares (prioriza las que funcionaron recientemente).
    /// Las obtenidas de la DHT no se reenvían hasta haber completado un handshake: cualquiera puede publicar basura ahí.
    /// </summary>
    public List<NodeAddress> GetShareable(int max, bool includePrivate)
    {
        lock (_gate)
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-7);
            return _entries.Values
                .Where(e => e.LastSuccess > cutoff || (e.Failures == 0 && e.Source != "dht"))
                .Select(e => e.ToNodeAddress())
                .Where(a => includePrivate || !IsPrivate(a.Endpoint.Ip))
                .OrderBy(_ => RandomNumberGenerator.GetInt32(int.MaxValue))
                .Take(max)
                .ToList();
        }
    }

    public List<AddressEntry> Snapshot()
    {
        lock (_gate) return _entries.Values.Select(e => e).ToList();
    }

    private sealed record Persisted(List<AddressEntry> Entries, Dictionary<string, DateTimeOffset> Bans);

    private void Load()
    {
        if (_path is null || !File.Exists(_path)) return;
        try
        {
            var data = JsonSerializer.Deserialize<Persisted>(File.ReadAllText(_path));
            if (data is null) return;
            foreach (var e in data.Entries.Take(MaxEntries))
            {
                if (!IPAddress.TryParse(e.Ip, out _) || e.Port is <= 0 or > 65535) continue;
                if (e.PeerId is not null && !P2P.PeerId.TryParse(e.PeerId, out _)) e.PeerId = null;
                if (e.CircuitTarget is not null && (e.PeerId is null || !P2P.PeerId.TryParse(e.CircuitTarget, out _))) continue;
                _entries[e.ToNodeAddress().Key] = e;
            }
            foreach (var (key, until) in data.Bans)
            {
                if (until > DateTimeOffset.UtcNow) _bans[key] = until;
            }
        }
        catch (JsonException)
        {
            // Archivo dañado: se reconstruye desde los nodos de arranque.
        }
    }

    public void Save()
    {
        if (_path is null) return;
        Persisted data;
        lock (_gate) data = new Persisted(_entries.Values.ToList(), new Dictionary<string, DateTimeOffset>(_bans));
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _path, overwrite: true);
    }
}
