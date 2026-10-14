using System.Globalization;
using System.Text;
using Shent.Core.Consensus;

namespace Shent.Core.Node;

/// <summary>
/// Configuración del nodo, leída de shent.conf (formato clave=valor, una por línea, # para comentarios).
/// Ejemplo:
///   network=mainnet
///   p2p_port=37373
///   bootstrap=/ip4/IP_PUBLICA_DEL_NODO/tcp/37373/p2p/PEER_ID_DEL_NODO
///   seed=seed1.ejemplo.net:37373
/// (IP_PUBLICA_DEL_NODO y PEER_ID_DEL_NODO se reemplazan por los datos reales que muestra el otro nodo).
/// </summary>
public sealed class NodeConfig
{
    public string Network { get; set; } = "mainnet";
    public string DataDirectory { get; set; } = DefaultDataDirectory();
    public int P2PPort { get; set; }
    public bool Listen { get; set; } = true;
    public int MaxOutbound { get; set; } = 8;
    public int MaxInbound { get; set; } = 32;
    public List<string> Seeds { get; set; } = [];
    public List<string> AddNodes { get; set; } = [];
    /// <summary>Nodos de arranque (multiaddr o host:puerto). Solo ayudan a descubrir pares; no validan nada por nadie.</summary>
    public List<string> Bootstrap { get; set; } = [];
    public bool LanDiscovery { get; set; } = true;
    /// <summary>Encontrar otros nodos por Internet usando la DHT pública de BitTorrent como punto de encuentro.</summary>
    public bool Dht { get; set; } = true;
    /// <summary>Routers de arranque de la DHT (host:puerto). Vacío = los públicos conocidos.</summary>
    public List<string> DhtRouters { get; set; } = [];
    public bool AllowLoopbackPeers { get; set; }
    /// <summary>Ofrecer relay de circuitos a nodos sin puertos abiertos (recomendado en un VPS con IP pública).</summary>
    public bool RelayService { get; set; }
    /// <summary>Si este nodo no es alcanzable desde Internet, reservar un circuito en un relay.</summary>
    public bool RelayClient { get; set; } = true;
    /// <summary>Intentar abrir el puerto P2P en el router con UPnP IGD.</summary>
    public bool Upnp { get; set; } = true;
    /// <summary>Dirección pública fija para anunciar (multiaddr o host:puerto), por ejemplo un dominio o IP de un VPS.</summary>
    public string? Announce { get; set; }
    /// <summary>
    /// Proxy SOCKS5 (host:puerto, p. ej. Tor en 127.0.0.1:9050). Con proxy, todas las conexiones salen por él
    /// y se apaga todo lo que revelaría la IP real: escucha, UPnP, DHT, red local y pruebas de alcanzabilidad.
    /// </summary>
    public string? Proxy { get; set; }
    /// <summary>Difusión Dandelion++: las transacciones propias viajan primero por un camino aleatorio de nodos.</summary>
    public bool Dandelion { get; set; } = true;
    public bool ProxyMode => Proxy is not null;
    /// <summary>API local de diagnóstico (solo lectura, solo 127.0.0.1).</summary>
    public bool Api { get; set; } = true;
    public int ApiPort { get; set; }
    public bool Mine { get; set; }
    public int MiningThreads { get; set; } = Math.Max(1, Environment.ProcessorCount / 2);
    public int MiningIntensity { get; set; } = 80;
    public string? MiningAddress { get; set; }
    public string UserAgent { get; set; } = "SHENT/0.1.0";

    public NetworkParams NetworkParams => Consensus.NetworkParams.FromName(Network);
    public int EffectivePort => P2PPort > 0 ? P2PPort : NetworkParams.DefaultPort;
    /// <summary>Puerto de la API local: por defecto el puerto P2P de la red + 7 (mainnet 37380, testnet 47380).</summary>
    public int EffectiveApiPort => ApiPort > 0 ? ApiPort : NetworkParams.DefaultPort + 7;
    public string NetworkDataDirectory => Path.Combine(DataDirectory, NetworkParams.Name);

    public static string DefaultDataDirectory()
    {
        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "SHENT");
        }
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SHENT");
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".shent");
    }

    public static string ConfigPath(string dataDirectory) => Path.Combine(dataDirectory, "shent.conf");

    public static NodeConfig Load(string dataDirectory)
    {
        var config = new NodeConfig { DataDirectory = dataDirectory };
        var path = ConfigPath(dataDirectory);
        if (!File.Exists(path)) return config;

        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim().ToLowerInvariant();
            var value = line[(eq + 1)..].Trim();
            config.Apply(key, value);
        }
        return config;
    }

    public void Apply(string key, string value)
    {
        switch (key)
        {
            case "network": Network = value; break;
            case "p2p_port" or "port": P2PPort = ParseInt(value, 0, 65535); break;
            case "listen": Listen = ParseBool(value); break;
            case "max_outbound": MaxOutbound = ParseInt(value, 0, 64); break;
            case "max_inbound": MaxInbound = ParseInt(value, 0, 512); break;
            case "seed": Seeds.Add(value); break;
            case "addnode": AddNodes.Add(value); break;
            case "bootstrap": AddList(Bootstrap, value); break;
            case "relay": RelayService = ParseBool(value); break;
            case "relay_client": RelayClient = ParseBool(value); break;
            case "upnp": Upnp = ParseBool(value); break;
            case "announce": Announce = value.Length == 0 ? null : value; break;
            case "proxy": Proxy = value.Length == 0 ? null : value; break;
            case "dandelion": Dandelion = ParseBool(value); break;
            case "api": Api = ParseBool(value); break;
            case "api_port": ApiPort = ParseInt(value, 0, 65535); break;
            case "lan_discovery": LanDiscovery = ParseBool(value); break;
            case "dht": Dht = ParseBool(value); break;
            case "dht_router": AddList(DhtRouters, value); break;
            case "mine": Mine = ParseBool(value); break;
            case "mining_threads": MiningThreads = ParseInt(value, 1, 1024); break;
            case "mining_intensity": MiningIntensity = ParseInt(value, 10, 100); break;
            case "mining_address": MiningAddress = value.Length == 0 ? null : value; break;
        }
    }

    /// <summary>Acepta varias direcciones separadas por coma, punto y coma o espacios.</summary>
    private static void AddList(List<string> list, string value)
    {
        foreach (var item in value.Split([',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!list.Contains(item)) list.Add(item);
        }
    }

    /// <summary>
    /// Variables de entorno (tienen prioridad sobre shent.conf):
    ///   BOOTSTRAP_NODES / SHENT_BOOTSTRAP_NODES   lista de multiaddr separadas por coma
    ///   P2P_PORT / SHENT_P2P_PORT                 puerto P2P
    ///   SHENT_RELAY=1                             ofrecer relay
    ///   SHENT_UPNP=0                              desactivar UPnP
    ///   SHENT_DHT=0                               desactivar el descubrimiento automático por la DHT
    ///   SHENT_ANNOUNCE                            dirección pública a anunciar
    ///   SHENT_PROXY                               proxy SOCKS5 (Tor: 127.0.0.1:9050)
    ///   SHENT_DANDELION=0                         desactivar Dandelion++
    ///   SHENT_API=0, SHENT_API_PORT               API local
    /// SHENT_NETWORK y SHENT_DATADIR se leen antes de cargar shent.conf (ver Shent.Node).
    /// </summary>
    public void ApplyEnvironment(Func<string, string?> getVariable)
    {
        string? Get(params string[] names) => names.Select(getVariable).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

        if (Get("SHENT_NETWORK") is { } network) Network = network;
        if (Get("BOOTSTRAP_NODES", "SHENT_BOOTSTRAP_NODES") is { } bootstrap) AddList(Bootstrap, bootstrap);
        if (Get("P2P_PORT", "SHENT_P2P_PORT") is { } port) Apply("p2p_port", port);
        if (Get("SHENT_RELAY") is { } relay) Apply("relay", relay);
        if (Get("SHENT_UPNP") is { } upnp) Apply("upnp", upnp);
        if (Get("SHENT_DHT") is { } dht) Apply("dht", dht);
        if (Get("SHENT_ANNOUNCE") is { } announce) Apply("announce", announce);
        if (Get("SHENT_PROXY") is { } proxy) Apply("proxy", proxy);
        if (Get("SHENT_DANDELION") is { } dandelion) Apply("dandelion", dandelion);
        if (Get("SHENT_API") is { } api) Apply("api", api);
        if (Get("SHENT_API_PORT") is { } apiPort) Apply("api_port", apiPort);
    }

    private static int ParseInt(string value, int min, int max) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? Math.Clamp(n, min, max) : min;

    private static bool ParseBool(string value) => value is "1" or "true" or "yes" or "si" or "sí";

    public void Save()
    {
        Directory.CreateDirectory(DataDirectory);
        var sb = new StringBuilder();
        sb.AppendLine("# Configuración del nodo SHENT");
        sb.AppendLine("# Red: mainnet (puerto 37373), testnet (47373) o regtest (57373, solo local)");
        sb.AppendLine($"network={Network}");
        sb.AppendLine("# Puerto P2P local (0 = puerto por defecto de la red)");
        sb.AppendLine($"p2p_port={P2PPort}");
        sb.AppendLine($"listen={(Listen ? 1 : 0)}");
        sb.AppendLine($"max_outbound={MaxOutbound}");
        sb.AppendLine($"max_inbound={MaxInbound}");
        sb.AppendLine($"lan_discovery={(LanDiscovery ? 1 : 0)}");
        sb.AppendLine("# Descubrimiento automático por Internet (DHT pública de BitTorrent como punto de encuentro).");
        sb.AppendLine("# La DHT solo entrega direcciones: cada nodo encontrado se verifica con el handshake firmado y cada bloque se valida.");
        sb.AppendLine($"dht={(Dht ? 1 : 0)}");
        foreach (var router in DhtRouters) sb.AppendLine($"dht_router={router}");
        sb.AppendLine("# Nodos de arranque: /ip4/IP_PUBLICA/tcp/PUERTO/p2p/PEER_ID (reemplazá con los datos reales del otro nodo).");
        sb.AppendLine("# Solo sirven para encontrar pares: no controlan saldos ni deciden qué bloques son válidos.");
        foreach (var node in Bootstrap) sb.AppendLine($"bootstrap={node}");
        sb.AppendLine("# Semillas DNS opcionales (host:puerto)");
        foreach (var seed in Seeds) sb.AppendLine($"seed={seed}");
        sb.AppendLine("# Pares fijos a los que conectarse siempre (multiaddr o IP:puerto)");
        foreach (var node in AddNodes) sb.AppendLine($"addnode={node}");
        sb.AppendLine("# Relay para nodos sin puertos abiertos (activalo en un servidor con IP pública)");
        sb.AppendLine($"relay={(RelayService ? 1 : 0)}");
        sb.AppendLine($"relay_client={(RelayClient ? 1 : 0)}");
        sb.AppendLine($"upnp={(Upnp ? 1 : 0)}");
        sb.AppendLine($"announce={Announce}");
        sb.AppendLine("# Privacidad: proxy SOCKS5 para salir por Tor (127.0.0.1:9050 con Tor, 127.0.0.1:9150 con Tor Browser).");
        sb.AppendLine("# Con proxy se desactivan la escucha, UPnP, la DHT y la red local, porque revelarían tu IP real.");
        sb.AppendLine($"proxy={Proxy}");
        sb.AppendLine("# Dandelion++: tus transacciones pasan primero por un camino aleatorio de nodos antes de difundirse.");
        sb.AppendLine($"dandelion={(Dandelion ? 1 : 0)}");
        sb.AppendLine("# API local de diagnóstico (solo lectura, escucha únicamente en 127.0.0.1)");
        sb.AppendLine($"api={(Api ? 1 : 0)}");
        sb.AppendLine($"api_port={ApiPort}");
        sb.AppendLine($"mine={(Mine ? 1 : 0)}");
        sb.AppendLine($"mining_threads={MiningThreads}");
        sb.AppendLine($"mining_intensity={MiningIntensity}");
        sb.AppendLine($"mining_address={MiningAddress}");
        var path = ConfigPath(DataDirectory);
        File.WriteAllText(path + ".tmp", sb.ToString());
        File.Move(path + ".tmp", path, overwrite: true);
    }
}
