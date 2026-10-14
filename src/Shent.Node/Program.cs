using System.Diagnostics;
using Shent.Core.Consensus;
using Shent.Core.Node;
using Shent.Core.P2P;

namespace Shent.Node;

internal static class Program
{
    private const string Help = """
        SHENT node — nodo P2P de la red SHENT (sin interfaz gráfica)

        Uso: shent-node [comando] [opciones]

        Comandos:
          run (por defecto)        Inicia el nodo
          id                       Muestra el Peer ID y las direcciones para compartir (sin iniciar el nodo)
          bench                    Mide la velocidad de minado de esta CPU
          help                     Muestra esta ayuda

        Opciones:
          --network <red>          mainnet | testnet | regtest
          --datadir <ruta>         Carpeta de datos (por defecto, la del usuario)
          --port <n>               Puerto P2P (mainnet 37373, testnet 47373)
          --bootstrap <multiaddr>  Nodo de arranque, p. ej. /ip4/IP_PUBLICA/tcp/37373/p2p/PEER_ID (repetible)
          --seed <host:puerto>     Semilla DNS para el descubrimiento inicial (repetible)
          --addnode <dirección>    Par fijo al que conectarse siempre (multiaddr o IP:puerto, repetible)
          --no-listen              No aceptar conexiones entrantes
          --no-lan                 Desactivar el descubrimiento en la red local
          --no-dht                 Desactivar el descubrimiento automático por Internet (DHT)
          --max-inbound <n>        Máximo de conexiones entrantes
          --relay                  Ofrecer relay a nodos sin puertos abiertos (para un servidor con IP pública)
          --no-relay-client        No usar relays aunque esta PC no sea accesible
          --no-upnp                No intentar abrir el puerto en el router por UPnP
          --announce <dirección>   Dirección pública fija a anunciar (p. ej. /dns4/mi.dominio/tcp/37373)
          --proxy <host:puerto>    Salir por un proxy SOCKS5, p. ej. Tor en 127.0.0.1:9050 (oculta tu IP;
                                   desactiva escucha, UPnP, DHT y red local)
          --no-dandelion           Difundir las transacciones propias directo, sin la fase privada de Dandelion++
          --api-port <n>           Puerto de la API local de diagnóstico (por defecto P2P + 7)
          --no-api                 Desactivar la API local
          --mine                   Minar en esta máquina
          --address <shent1...>    Dirección que recibe las recompensas de minado
          --threads <n>            Hilos de minado
          --intensity <10-100>     Porcentaje de uso de CPU por hilo

        Variables de entorno (tienen prioridad sobre shent.conf; las opciones de la línea de comandos sobre ambas):
          BOOTSTRAP_NODES          Lista de nodos de arranque separados por coma
          P2P_PORT                 Puerto P2P
          SHENT_NETWORK, SHENT_DATADIR, SHENT_RELAY, SHENT_UPNP, SHENT_DHT, SHENT_ANNOUNCE, SHENT_API, SHENT_API_PORT,
          SHENT_PROXY, SHENT_DANDELION

        Los nodos se encuentran solos: por la DHT pública (Internet), por la red local y por intercambio de
        direcciones entre pares. Los nodos de arranque son opcionales.

        La configuración también puede definirse en <datadir>/shent.conf (clave=valor).
        SHENT es una moneda comunitaria.
        """;

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var command = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : "run";
        var options = command == "run" && (args.Length == 0 || args[0].StartsWith("--")) ? args : args.Skip(1).ToArray();

        switch (command)
        {
            case "help" or "-h" or "--help":
                Console.WriteLine(Help);
                return 0;
            case "bench":
                return Bench(options);
            case "id":
                return ShowIdentity(options);
            case "run":
                return await RunAsync(options);
            default:
                Console.Error.WriteLine($"Comando desconocido: {command}\n");
                Console.WriteLine(Help);
                return 1;
        }
    }

    private static NodeConfig BuildConfig(string[] args)
    {
        var dataDir = Environment.GetEnvironmentVariable("SHENT_DATADIR") is { Length: > 0 } envDir ? envDir : NodeConfig.DefaultDataDirectory();
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--datadir") dataDir = args[i + 1];
        }
        var config = NodeConfig.Load(dataDir);
        config.ApplyEnvironment(Environment.GetEnvironmentVariable);

        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Falta el valor de {args[i]}.");
            switch (args[i])
            {
                case "--datadir": Next(); break;
                case "--network": config.Network = Next(); break;
                case "--port": config.Apply("p2p_port", Next()); break;
                case "--bootstrap": config.Apply("bootstrap", Next()); break;
                case "--seed": config.Seeds.Add(Next()); break;
                case "--addnode": config.AddNodes.Add(Next()); break;
                case "--no-listen": config.Listen = false; break;
                case "--no-lan": config.LanDiscovery = false; break;
                case "--no-dht": config.Dht = false; break;
                case "--max-inbound": config.Apply("max_inbound", Next()); break;
                case "--relay": config.RelayService = true; break;
                case "--no-relay-client": config.RelayClient = false; break;
                case "--no-upnp": config.Upnp = false; break;
                case "--announce": config.Apply("announce", Next()); break;
                case "--proxy": config.Apply("proxy", Next()); break;
                case "--no-dandelion": config.Dandelion = false; break;
                case "--api-port": config.Apply("api_port", Next()); break;
                case "--no-api": config.Api = false; break;
                case "--mine": config.Mine = true; break;
                case "--address": config.MiningAddress = Next(); break;
                case "--threads": config.Apply("mining_threads", Next()); break;
                case "--intensity": config.Apply("mining_intensity", Next()); break;
                default: throw new ArgumentException($"Opción desconocida: {args[i]}");
            }
        }
        _ = config.NetworkParams;
        return config;
    }

    private static async Task<int> RunAsync(string[] args)
    {
        NodeConfig config;
        try
        {
            config = BuildConfig(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        var log = new NodeLog();
        log.Written += entry =>
        {
            var color = entry.Level switch
            {
                LogLevel.Success => ConsoleColor.Green,
                LogLevel.Warning => ConsoleColor.Yellow,
                LogLevel.Error => ConsoleColor.Red,
                _ => ConsoleColor.Gray,
            };
            lock (Console.Out)
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"{entry.Time:HH:mm:ss} [{entry.Category}] ");
                Console.ForegroundColor = color;
                Console.WriteLine(entry.Message);
                Console.ResetColor();
            }
        };

        Console.WriteLine("SHENT — moneda comunitaria.");
        ShentNode started;
        try
        {
            started = ShentNode.Start(config, log);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        await using var node = started;
        if (config.Bootstrap.Count == 0 && config.AddNodes.Count == 0 && config.Seeds.Count == 0 && node.Network.Seeds.Count == 0)
        {
            log.Info("p2p", node.P2P.Dht.Running
                ? "Sin nodos de arranque: no hacen falta, el nodo busca a los demás solo por la DHT y la red local. Direcciones de este nodo:"
                : "Sin nodos de arranque: este nodo espera conexiones. Para que otro nodo se una, compartile una de estas direcciones " +
                  "(reemplazá la IP local por tu IP pública si es por Internet):");
        }
        foreach (var address in node.P2P.GetLocalAddresses()) log.Info("p2p", $"  {address}");
        var announcedPublic = new HashSet<string>();

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            shutdown.Cancel();
        };

        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), shutdown.Token);
                var s = node.GetStatus();
                var mining = node.Miner.IsRunning ? $" | minando {node.Miner.Hashrate:F0} H/s" : string.Empty;
                var relayed = s.RelayedPeers > 0 ? $", {s.RelayedPeers} vía relay" : string.Empty;
                log.Info("estado",
                    $"altura {s.Height}/{s.BestKnownHeight} | pares {s.PeersConnected} ({s.Inbound} ent.{relayed}) | " +
                    $"alcanzable: {ReachabilityText(s.Reachability)} | red {FormatRate(s.NetworkHashrate)} | " +
                    $"emitido {Money.Format(s.IssuedSupply)} SHENT | mempool {s.MempoolCount}{mining}");
                foreach (var address in node.P2P.GetPublicAddresses())
                {
                    if (announcedPublic.Add(address)) log.Info("p2p", $"Dirección para compartir por Internet: {address}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            log.Info("nodo", "Deteniendo…");
        }
        return 0;
    }

    private static string ReachabilityText(Reachability reachability) => reachability switch
    {
        Reachability.Public => "sí",
        Reachability.Private => "no (se usa relay)",
        _ => "comprobando",
    };

    /// <summary>Muestra el Peer ID persistente (lo crea si no existe) sin iniciar el nodo.</summary>
    private static int ShowIdentity(string[] args)
    {
        NodeConfig config;
        try
        {
            config = BuildConfig(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        Directory.CreateDirectory(config.NetworkDataDirectory);
        using var identity = NodeIdentity.LoadOrCreate(ShentNode.IdentityPath(config), Console.Error.WriteLine);
        var port = config.EffectivePort;
        Console.WriteLine($"Red:      {config.NetworkParams.Name}");
        Console.WriteLine($"Peer ID:  {identity.PeerId}");
        Console.WriteLine($"Puerto:   TCP {port}");
        Console.WriteLine();
        Console.WriteLine("Dirección para compartir (reemplazá IP_PUBLICA por la IP pública de esta conexión o por un dominio):");
        Console.WriteLine($"  /ip4/IP_PUBLICA/tcp/{port}/p2p/{identity.PeerId}");
        Console.WriteLine($"  /dns4/TU_DOMINIO/tcp/{port}/p2p/{identity.PeerId}");
        Console.WriteLine();
        Console.WriteLine("El archivo de identidad no contiene fondos ni claves de billetera: solo identifica a este nodo en la red.");
        return 0;
    }

    private static int Bench(string[] args)
    {
        var network = NetworkParams.Mainnet;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--network") network = NetworkParams.FromName(args[i + 1]);
        }
        Console.WriteLine($"Midiendo Argon2id ({network.PowMemoryKiB} KiB) en {Environment.ProcessorCount} núcleos lógicos…");
        var pow = network.CreatePowHasher();
        foreach (var threads in new[] { 1, Math.Max(1, Environment.ProcessorCount / 2), Environment.ProcessorCount }.Distinct())
        {
            long total = 0;
            var sw = Stopwatch.StartNew();
            Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
            {
                var header = new byte[100];
                header[0] = (byte)t;
                var output = new byte[32];
                long n = 0;
                while (sw.ElapsedMilliseconds < 3000)
                {
                    header[1]++;
                    header[2] = (byte)(n >> 8);
                    pow.Hash(header, output);
                    n++;
                }
                Interlocked.Add(ref total, n);
            });
            Console.WriteLine($"  {threads,2} hilo(s): {total / sw.Elapsed.TotalSeconds,8:F0} H/s");
        }
        return 0;
    }

    private static string FormatRate(double hps) => hps switch
    {
        >= 1e6 => $"{hps / 1e6:F2} MH/s",
        >= 1e3 => $"{hps / 1e3:F2} kH/s",
        _ => $"{hps:F0} H/s",
    };
}
