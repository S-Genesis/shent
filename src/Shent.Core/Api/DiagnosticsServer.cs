using System.Net;
using System.Net.Sockets;
using System.Text;
using TextEncoding = System.Text.Encoding;
using System.Text.Json;
using System.Text.Json.Serialization;
using Shent.Core.Node;

namespace Shent.Core.Api;

/// <summary>
/// API HTTP local de solo lectura para diagnóstico. Escucha únicamente en 127.0.0.1, acepta solo GET,
/// limita el tamaño de la petición y exige un encabezado Host local (evita ataques de DNS rebinding
/// desde páginas web). No tiene rutas que modifiquen el nodo ni expone claves, semillas ni billeteras.
/// </summary>
public sealed class DiagnosticsServer : IAsyncDisposable
{
    private const int MaxRequestBytes = 8 * 1024;
    private const int MaxConcurrent = 16;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly IReadOnlyDictionary<string, Func<object>> _routes;
    private readonly NodeLog _log;
    private readonly int _requestedPort;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _slots = new(MaxConcurrent);
    private TcpListener? _listener;

    public int Port { get; private set; }

    public DiagnosticsServer(int port, IReadOnlyDictionary<string, Func<object>> routes, NodeLog log)
    {
        _requestedPort = port;
        _routes = routes;
        _log = log;
    }

    public bool Start(bool quiet = false)
    {
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, _requestedPort);
            _listener.Start();
        }
        catch (SocketException ex)
        {
            if (!quiet)
            {
                _log.Warn("api", $"No se pudo iniciar la API local en 127.0.0.1:{_requestedPort} ({ex.SocketErrorCode}). Se reintenta cada 30 s.");
            }
            _listener = null;
            return false;
        }
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _log.Info("api", $"API local de diagnóstico en http://127.0.0.1:{Port}/api/p2p/status (solo lectura, solo esta PC).");
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        return true;
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _listener is not null)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                if (token.IsCancellationRequested) return;
                continue;
            }
            if (!await _slots.WaitAsync(0, token).ConfigureAwait(false))
            {
                client.Dispose();
                continue;
            }
            _ = Task.Run(async () =>
            {
                try { await HandleAsync(client, token).ConfigureAwait(false); }
                finally
                {
                    client.Dispose();
                    _slots.Release();
                }
            });
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var stream = client.GetStream();
            var request = await ReadHeadAsync(stream, timeout.Token).ConfigureAwait(false);
            var (status, body) = request is null ? (400, Error("Petición inválida.")) : Route(request);
            await WriteResponseAsync(stream, status, body, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private sealed record Request(string Method, string Path, string? Host);

    private static async Task<Request?> ReadHeadAsync(NetworkStream stream, CancellationToken token)
    {
        var buffer = new byte[MaxRequestBytes];
        var length = 0;
        while (length < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(length), token).ConfigureAwait(false);
            if (n == 0) break;
            length += n;
            if (buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8) >= 0) break;
        }
        var text = TextEncoding.ASCII.GetString(buffer, 0, length);
        var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (end < 0) return null;
        var lines = text[..end].Split("\r\n");
        var parts = lines[0].Split(' ');
        if (parts.Length != 3 || !parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal)) return null;
        string? host = null;
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon > 0 && line[..colon].Trim().Equals("Host", StringComparison.OrdinalIgnoreCase)) host = line[(colon + 1)..].Trim();
        }
        var path = parts[1];
        var query = path.IndexOf('?');
        if (query >= 0) path = path[..query];
        return new Request(parts[0], path.TrimEnd('/'), host);
    }

    public static bool IsLocalHost(string? host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        var name = host;
        if (name.StartsWith('['))
        {
            var close = name.IndexOf(']');
            if (close < 0) return false;
            name = name[1..close];
        }
        else if (name.Count(c => c == ':') == 1)
        {
            name = name[..name.IndexOf(':')];
        }
        return name.Equals("localhost", StringComparison.OrdinalIgnoreCase) || name == "127.0.0.1" || name == "::1";
    }

    private (int Status, string Body) Route(Request request)
    {
        if (!IsLocalHost(request.Host)) return (403, Error("Solo se aceptan peticiones dirigidas a localhost."));
        if (request.Method != "GET") return (405, Error("Solo se permite GET: esta API es de solo lectura."));
        if (request.Path is "" or "/api" or "/api/p2p")
        {
            return (200, JsonSerializer.Serialize(new { endpoints = _routes.Keys.Order().ToArray() }, Json));
        }
        if (!_routes.TryGetValue(request.Path, out var handler)) return (404, Error("Ruta no encontrada."));
        try
        {
            return (200, JsonSerializer.Serialize(handler(), Json));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            return (503, Error("El nodo no está disponible."));
        }
    }

    private static string Error(string message) => JsonSerializer.Serialize(new { error = message }, Json);

    private static async Task WriteResponseAsync(NetworkStream stream, int status, string body, CancellationToken token)
    {
        var reason = status switch
        {
            200 => "OK",
            400 => "Bad Request",
            403 => "Forbidden",
            404 => "Not Found",
            405 => "Method Not Allowed",
            _ => "Service Unavailable",
        };
        var payload = TextEncoding.UTF8.GetBytes(body);
        var head = $"HTTP/1.1 {status} {reason}\r\n" +
                   "Content-Type: application/json; charset=utf-8\r\n" +
                   $"Content-Length: {payload.Length}\r\n" +
                   "Cache-Control: no-store\r\n" +
                   "X-Content-Type-Options: nosniff\r\n" +
                   (status == 405 ? "Allow: GET\r\n" : string.Empty) +
                   "Connection: close\r\n\r\n";
        await stream.WriteAsync(TextEncoding.ASCII.GetBytes(head), token).ConfigureAwait(false);
        await stream.WriteAsync(payload, token).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener?.Stop();
        _cts.Dispose();
        return ValueTask.CompletedTask;
    }
}
