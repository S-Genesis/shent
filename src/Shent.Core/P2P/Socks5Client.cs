using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Shent.Core.P2P;

public sealed class Socks5Exception(string message) : Exception(message);

/// <summary>
/// Cliente SOCKS5 (RFC 1928/1929) para salir a la red P2P a través de Tor u otro proxy, sin que los
/// demás nodos vean la IP real de esta PC.
///
/// Cada conexión se autentica con usuario y contraseña aleatorios: Tor (IsolateSOCKSAuth, activo por
/// defecto) usa entonces un circuito distinto por par, así dos nodos no pueden vincular que hablan con
/// el mismo cliente por compartir salida. Los nombres DNS se resuelven a través de Tor con la extensión
/// RESOLVE (0xF0) para que la consulta no salga por el DNS del proveedor.
/// </summary>
public static class Socks5Client
{
    private const byte Version = 5;
    private const byte MethodNoAuth = 0;
    private const byte MethodUserPass = 2;
    private const byte CommandConnect = 1;
    private const byte CommandTorResolve = 0xF0;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Abre un túnel TCP hasta <paramref name="target"/> por el proxy. Devuelve el cliente ya conectado.</summary>
    public static async Task<TcpClient> ConnectAsync(IPEndPoint proxy, IPEndPoint target, CancellationToken cancellation)
    {
        var client = new TcpClient(proxy.AddressFamily);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(HandshakeTimeout);
            await client.ConnectAsync(proxy, timeout.Token).ConfigureAwait(false);
            var stream = client.GetStream();
            await NegotiateAsync(stream, timeout.Token).ConfigureAwait(false);
            await SendRequestAsync(stream, CommandConnect, AddressField(target.Address), (ushort)target.Port, timeout.Token).ConfigureAwait(false);
            await ReadReplyAsync(stream, timeout.Token).ConfigureAwait(false);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>Resuelve un nombre por Tor (extensión RESOLVE). Otros proxies no la soportan: devuelve vacío.</summary>
    public static async Task<IPAddress[]> ResolveAsync(IPEndPoint proxy, string host, CancellationToken cancellation)
    {
        using var client = new TcpClient(proxy.AddressFamily);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(HandshakeTimeout);
            await client.ConnectAsync(proxy, timeout.Token).ConfigureAwait(false);
            var stream = client.GetStream();
            await NegotiateAsync(stream, timeout.Token).ConfigureAwait(false);
            var name = System.Text.Encoding.ASCII.GetBytes(host);
            if (name.Length is 0 or > 255) return [];
            var field = new byte[2 + name.Length];
            field[0] = 3;
            field[1] = (byte)name.Length;
            name.CopyTo(field, 2);
            await SendRequestAsync(stream, CommandTorResolve, field, 0, timeout.Token).ConfigureAwait(false);
            var bound = await ReadReplyAsync(stream, timeout.Token).ConfigureAwait(false);
            return bound is null ? [] : [bound];
        }
        catch (Exception ex) when (ex is Socks5Exception or SocketException or IOException or EndOfStreamException or OperationCanceledException)
        {
            if (cancellation.IsCancellationRequested) throw;
            return [];
        }
    }

    private static async Task NegotiateAsync(Stream stream, CancellationToken token)
    {
        await stream.WriteAsync(new byte[] { Version, 2, MethodNoAuth, MethodUserPass }, token).ConfigureAwait(false);
        var choice = new byte[2];
        await stream.ReadExactlyAsync(choice, token).ConfigureAwait(false);
        if (choice[0] != Version) throw new Socks5Exception("El proxy no habla SOCKS5.");
        switch (choice[1])
        {
            case MethodNoAuth:
                return;
            case MethodUserPass:
            {
                // Credenciales aleatorias: no son secretas, solo separan circuitos de Tor por conexión.
                var user = System.Text.Encoding.ASCII.GetBytes(Convert.ToHexString(RandomNumberGenerator.GetBytes(8)));
                var pass = System.Text.Encoding.ASCII.GetBytes(Convert.ToHexString(RandomNumberGenerator.GetBytes(8)));
                var auth = new byte[3 + user.Length + pass.Length];
                auth[0] = 1;
                auth[1] = (byte)user.Length;
                user.CopyTo(auth, 2);
                auth[2 + user.Length] = (byte)pass.Length;
                pass.CopyTo(auth, 3 + user.Length);
                await stream.WriteAsync(auth, token).ConfigureAwait(false);
                var status = new byte[2];
                await stream.ReadExactlyAsync(status, token).ConfigureAwait(false);
                if (status[1] != 0) throw new Socks5Exception("El proxy rechazó la autenticación.");
                return;
            }
            default:
                throw new Socks5Exception("El proxy exige un método de autenticación no soportado.");
        }
    }

    private static byte[] AddressField(IPAddress address)
    {
        var ip = PeerAddress.Normalize(address);
        var bytes = ip.GetAddressBytes();
        var field = new byte[1 + bytes.Length];
        field[0] = ip.AddressFamily == AddressFamily.InterNetwork ? (byte)1 : (byte)4;
        bytes.CopyTo(field, 1);
        return field;
    }

    private static async Task SendRequestAsync(Stream stream, byte command, byte[] addressField, ushort port, CancellationToken token)
    {
        var request = new byte[3 + addressField.Length + 2];
        request[0] = Version;
        request[1] = command;
        request[2] = 0;
        addressField.CopyTo(request, 3);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(3 + addressField.Length), port);
        await stream.WriteAsync(request, token).ConfigureAwait(false);
    }

    private static async Task<IPAddress?> ReadReplyAsync(Stream stream, CancellationToken token)
    {
        var head = new byte[4];
        await stream.ReadExactlyAsync(head, token).ConfigureAwait(false);
        if (head[0] != Version) throw new Socks5Exception("Respuesta SOCKS5 inválida.");
        if (head[1] != 0) throw new Socks5Exception(ReplyText(head[1]));
        IPAddress? bound = null;
        switch (head[3])
        {
            case 1:
            {
                var ip = new byte[4];
                await stream.ReadExactlyAsync(ip, token).ConfigureAwait(false);
                bound = new IPAddress(ip);
                break;
            }
            case 4:
            {
                var ip = new byte[16];
                await stream.ReadExactlyAsync(ip, token).ConfigureAwait(false);
                bound = new IPAddress(ip);
                break;
            }
            case 3:
            {
                var length = new byte[1];
                await stream.ReadExactlyAsync(length, token).ConfigureAwait(false);
                await stream.ReadExactlyAsync(new byte[length[0]], token).ConfigureAwait(false);
                break;
            }
            default:
                throw new Socks5Exception("Tipo de dirección SOCKS5 desconocido.");
        }
        await stream.ReadExactlyAsync(new byte[2], token).ConfigureAwait(false);
        return bound;
    }

    private static string ReplyText(byte code) => code switch
    {
        1 => "El proxy falló.",
        2 => "El proxy no permite esa conexión.",
        3 => "Red inaccesible desde el proxy.",
        4 => "Nodo inaccesible desde el proxy.",
        5 => "Conexión rechazada por el nodo.",
        6 => "Tiempo agotado en el proxy.",
        7 => "Comando no soportado por el proxy.",
        8 => "Tipo de dirección no soportado por el proxy.",
        _ => $"Error SOCKS5 {code}.",
    };
}
