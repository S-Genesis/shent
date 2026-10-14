using System.Net;
using System.Net.Sockets;

namespace Shent.Core.P2P;

/// <summary>
/// Dirección de conexión de un nodo, con formato compatible con multiaddr:
///   /ip4/IP/tcp/PUERTO/p2p/PEER_ID                                  conexión directa
///   /ip4/IP_RELAY/tcp/PUERTO/p2p/ID_RELAY/p2p-circuit/p2p/PEER_ID   a través de un relay
/// El Peer ID es opcional en direcciones aprendidas de la red, pero cuando está presente el
/// handshake exige que el nodo remoto lo demuestre con su firma.
/// </summary>
public sealed record NodeAddress(PeerAddress Endpoint, PeerId? PeerId = null, PeerId? CircuitTarget = null)
{
    public bool IsCircuit => CircuitTarget is not null;

    /// <summary>Identidad del nodo final (el destino en un circuito, o el propio nodo en una conexión directa).</summary>
    public PeerId? NodeId => CircuitTarget ?? PeerId;

    public string Key => IsCircuit ? $"{Endpoint.Key}/p2p-circuit/{CircuitTarget}" : Endpoint.Key;

    public string ToMultiaddr()
    {
        var ip = Endpoint.Ip;
        var proto = ip.AddressFamily == AddressFamily.InterNetworkV6 && !ip.IsIPv4MappedToIPv6 ? "ip6" : "ip4";
        var text = $"/{proto}/{PeerAddress.Normalize(ip)}/tcp/{Endpoint.Port}";
        if (PeerId is not null) text += $"/p2p/{PeerId}";
        if (CircuitTarget is not null) text += $"/p2p-circuit/p2p/{CircuitTarget}";
        return text;
    }

    public override string ToString() => ToMultiaddr();

    public static implicit operator NodeAddress(PeerAddress address) => new(address);
}

/// <summary>Dirección escrita por el usuario o la configuración, que puede contener un nombre DNS a resolver.</summary>
public sealed record AddressSpec(string Host, int Port, PeerId? PeerId, PeerId? CircuitTarget)
{
    public bool IsLiteral => IPAddress.TryParse(Host, out _);

    /// <summary>
    /// Acepta multiaddr (/ip4, /ip6, /dns, /dns4, /dns6 + /tcp + /p2p opcional + /p2p-circuit/p2p/ID opcional)
    /// o el formato clásico host:puerto / IP:puerto / [IPv6]:puerto.
    /// </summary>
    public static bool TryParse(string? text, int defaultPort, out AddressSpec? spec, out string? error)
    {
        spec = null;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Dirección vacía.";
            return false;
        }
        text = text.Trim();
        return text.StartsWith('/') ? TryParseMultiaddr(text, defaultPort, out spec, out error) : TryParseHostPort(text, defaultPort, out spec, out error);
    }

    private static bool TryParseHostPort(string text, int defaultPort, out AddressSpec? spec, out string? error)
    {
        spec = null;
        error = null;
        if (IPEndPoint.TryParse(text, out var ep) && (text.Contains('.') || text.Contains(':')))
        {
            var port = ep.Port == 0 ? defaultPort : ep.Port;
            spec = new AddressSpec(PeerAddress.Normalize(ep.Address).ToString(), port, null, null);
            return true;
        }
        var colon = text.LastIndexOf(':');
        var host = colon > 0 ? text[..colon] : text;
        var portValue = defaultPort;
        if (colon > 0 && (!int.TryParse(text[(colon + 1)..], out portValue) || portValue is < 1 or > 65535))
        {
            error = $"Puerto inválido en «{text}».";
            return false;
        }
        if (!IsValidHostName(host))
        {
            error = $"Nombre de host inválido: «{host}».";
            return false;
        }
        spec = new AddressSpec(host, portValue, null, null);
        return true;
    }

    private static bool TryParseMultiaddr(string text, int defaultPort, out AddressSpec? spec, out string? error)
    {
        spec = null;
        error = null;
        var parts = text.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string? host = null;
        var port = defaultPort;
        PeerId? peer = null;
        PeerId? target = null;
        var circuit = false;
        for (var i = 0; i < parts.Length; i++)
        {
            var proto = parts[i].ToLowerInvariant();
            if (proto == "p2p-circuit")
            {
                if (peer is null)
                {
                    error = "Un circuito de relay necesita el Peer ID del relay antes de /p2p-circuit.";
                    return false;
                }
                circuit = true;
                continue;
            }
            if (i + 1 >= parts.Length)
            {
                error = $"Falta el valor de /{proto}.";
                return false;
            }
            var value = parts[++i];
            switch (proto)
            {
                case "ip4":
                    if (!IPAddress.TryParse(value, out var v4) || v4.AddressFamily != AddressFamily.InterNetwork) { error = $"IPv4 inválida: {value}"; return false; }
                    host = value;
                    break;
                case "ip6":
                    if (!IPAddress.TryParse(value, out var v6) || v6.AddressFamily != AddressFamily.InterNetworkV6) { error = $"IPv6 inválida: {value}"; return false; }
                    host = value;
                    break;
                case "dns" or "dns4" or "dns6":
                    if (!IsValidHostName(value)) { error = $"Nombre DNS inválido: {value}"; return false; }
                    host = value;
                    break;
                case "tcp":
                    if (!int.TryParse(value, out port) || port is < 1 or > 65535) { error = $"Puerto TCP inválido: {value}"; return false; }
                    break;
                case "p2p" or "ipfs":
                    if (!PeerId.TryParse(value, out var id)) { error = $"Peer ID inválido: {value}"; return false; }
                    if (circuit) target = id;
                    else peer = id;
                    break;
                default:
                    error = $"Protocolo no soportado en la dirección: /{proto} (usá /ip4, /ip6, /dns4, /tcp, /p2p).";
                    return false;
            }
        }
        if (host is null)
        {
            error = "La dirección necesita /ip4, /ip6 o /dns4.";
            return false;
        }
        if (circuit && target is null)
        {
            error = "Falta el Peer ID del destino después de /p2p-circuit.";
            return false;
        }
        spec = new AddressSpec(host, port, peer, target);
        return true;
    }

    private static bool IsValidHostName(string host) =>
        host.Length is > 0 and <= 253 && host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-') && !host.StartsWith('-');

    /// <param name="resolver">Resolución alternativa (p. ej. por Tor); si es null se usa el DNS del sistema.</param>
    public async Task<List<NodeAddress>> ResolveAsync(CancellationToken cancellation = default,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolver = null)
    {
        IPAddress[] ips;
        if (IPAddress.TryParse(Host, out var literal))
        {
            ips = [literal];
        }
        else
        {
            try
            {
                ips = resolver is not null
                    ? await resolver(Host, cancellation).WaitAsync(TimeSpan.FromSeconds(30), cancellation).ConfigureAwait(false)
                    : await Dns.GetHostAddressesAsync(Host, cancellation).WaitAsync(TimeSpan.FromSeconds(5), cancellation).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or TimeoutException or ArgumentException)
            {
                return [];
            }
        }
        return ips
            .OrderBy(ip => ip.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .Take(8)
            .Select(ip => new NodeAddress(new PeerAddress(PeerAddress.Normalize(ip), Port), PeerId, CircuitTarget))
            .ToList();
    }

    public override string ToString()
    {
        var proto = IPAddress.TryParse(Host, out var ip) ? (ip.AddressFamily == AddressFamily.InterNetworkV6 ? "ip6" : "ip4") : "dns4";
        var text = $"/{proto}/{Host}/tcp/{Port}";
        if (PeerId is not null) text += $"/p2p/{PeerId}";
        if (CircuitTarget is not null) text += $"/p2p-circuit/p2p/{CircuitTarget}";
        return text;
    }
}
