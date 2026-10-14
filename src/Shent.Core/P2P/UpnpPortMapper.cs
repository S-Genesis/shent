using System.Net;
using System.Net.Sockets;
using System.Security;
using System.Text;
using TextEncoding = System.Text.Encoding;
using System.Xml.Linq;

namespace Shent.Core.P2P;

public enum UpnpState
{
    Disabled,
    Searching,
    NotFound,
    Mapped,
    Failed,
}

/// <summary>
/// Apertura automática del puerto P2P en el router mediante UPnP IGD (SSDP + SOAP).
/// Funciona solo si el router tiene UPnP activado y si el router tiene la IP pública real. Si el
/// proveedor usa CGNAT, el router recibe una IP privada (por ejemplo 100.64.x.x) y abrir el puerto en
/// él no alcanza: en ese caso hace falta un relay o pedir una IP pública al proveedor.
/// </summary>
public sealed class UpnpPortMapper : IAsyncDisposable
{
    private const string Description = "SHENT P2P";
    private static readonly IPEndPoint SsdpEndpoint = new(IPAddress.Parse("239.255.255.250"), 1900);
    private static readonly string[] SearchTargets =
    [
        "urn:schemas-upnp-org:device:InternetGatewayDevice:2",
        "urn:schemas-upnp-org:device:InternetGatewayDevice:1",
        "urn:schemas-upnp-org:service:WANIPConnection:1",
    ];

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private Uri? _controlUrl;
    private string? _serviceType;
    private IPAddress? _localIp;
    private int _port;

    public UpnpState State { get; private set; } = UpnpState.Disabled;
    public IPAddress? ExternalIp { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>El router tiene una IP privada o de CGNAT: el puerto abierto en él no es accesible desde Internet.</summary>
    public bool BehindCarrierNat => ExternalIp is not null && AddressBook.IsPrivate(ExternalIp);

    public async Task<bool> MapAsync(int port, CancellationToken cancellation)
    {
        _port = port;
        State = UpnpState.Searching;
        try
        {
            if (_controlUrl is null && !await DiscoverAsync(cancellation).ConfigureAwait(false))
            {
                State = UpnpState.NotFound;
                LastError = "No se encontró un router con UPnP IGD en la red local.";
                return false;
            }
            var ok = await AddMappingAsync(3600, cancellation).ConfigureAwait(false)
                  || await AddMappingAsync(0, cancellation).ConfigureAwait(false);
            if (!ok)
            {
                State = UpnpState.Failed;
                return false;
            }
            ExternalIp = await GetExternalIpAsync(cancellation).ConfigureAwait(false);
            State = UpnpState.Mapped;
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or IOException or TaskCanceledException or System.Xml.XmlException)
        {
            State = UpnpState.Failed;
            LastError = ex.Message;
            return false;
        }
    }

    private async Task<bool> DiscoverAsync(CancellationToken cancellation)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        // SSDP va por UDP multicast y los paquetes se pierden con frecuencia: se envía dos veces.
        for (var round = 0; round < 2; round++)
        {
            foreach (var target in SearchTargets)
            {
                var request = $"M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: {target}\r\n\r\n";
                var bytes = TextEncoding.ASCII.GetBytes(request);
                await udp.SendAsync(bytes, SsdpEndpoint, cancellation).ConfigureAwait(false);
            }
            if (round == 0) await Task.Delay(600, cancellation).ConfigureAwait(false);
        }

        var deadline = DateTime.UtcNow.AddSeconds(4);
        var tried = new HashSet<string>();
        while (DateTime.UtcNow < deadline)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            cts.CancelAfter(deadline - DateTime.UtcNow);
            UdpReceiveResult response;
            try
            {
                response = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                break;
            }
            var location = ParseLocation(TextEncoding.ASCII.GetString(response.Buffer));
            if (location is null || !tried.Add(location.ToString())) continue;
            // Solo se acepta un router de la red local que responde desde la misma IP que su descripción.
            if (!AddressBook.IsPrivate(response.RemoteEndPoint.Address)) continue;
            if (!IPAddress.TryParse(location.Host, out var host) || !host.Equals(response.RemoteEndPoint.Address)) continue;
            try
            {
                var xml = await _http.GetStringAsync(location, cancellation).ConfigureAwait(false);
                var service = FindWanService(xml, location);
                if (service is null) continue;
                _controlUrl = service.Value.ControlUrl;
                _serviceType = service.Value.ServiceType;
                _localIp = LocalAddressFor(response.RemoteEndPoint.Address);
                return _localIp is not null;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Xml.XmlException)
            {
            }
        }
        return false;
    }

    public static Uri? ParseLocation(string ssdpResponse)
    {
        foreach (var line in ssdpResponse.Split("\r\n"))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            if (!line[..colon].Trim().Equals("LOCATION", StringComparison.OrdinalIgnoreCase)) continue;
            return Uri.TryCreate(line[(colon + 1)..].Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp ? uri : null;
        }
        return null;
    }

    /// <summary>Busca en la descripción del dispositivo el servicio WANIPConnection o WANPPPConnection.</summary>
    public static (Uri ControlUrl, string ServiceType)? FindWanService(string deviceXml, Uri location)
    {
        var doc = XDocument.Parse(deviceXml);
        var ns = doc.Root?.Name.Namespace ?? XNamespace.None;
        var baseText = doc.Root?.Element(ns + "URLBase")?.Value;
        var baseUri = !string.IsNullOrWhiteSpace(baseText) && Uri.TryCreate(baseText, UriKind.Absolute, out var parsed) ? parsed : location;
        foreach (var service in doc.Descendants(ns + "service"))
        {
            var type = service.Element(ns + "serviceType")?.Value?.Trim() ?? string.Empty;
            if (!type.Contains("WANIPConnection", StringComparison.Ordinal) && !type.Contains("WANPPPConnection", StringComparison.Ordinal)) continue;
            var control = service.Element(ns + "controlURL")?.Value?.Trim();
            if (string.IsNullOrEmpty(control)) continue;
            if (!Uri.TryCreate(baseUri, control, out var controlUri)) continue;
            if (controlUri.Host != location.Host) continue;
            return (controlUri, type);
        }
        return null;
    }

    private static IPAddress? LocalAddressFor(IPAddress gateway)
    {
        try
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect(gateway, 1900);
            return (probe.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private async Task<bool> AddMappingAsync(int leaseSeconds, CancellationToken cancellation)
    {
        var args = new StringBuilder()
            .Append("<NewRemoteHost></NewRemoteHost>")
            .Append($"<NewExternalPort>{_port}</NewExternalPort>")
            .Append("<NewProtocol>TCP</NewProtocol>")
            .Append($"<NewInternalPort>{_port}</NewInternalPort>")
            .Append($"<NewInternalClient>{_localIp}</NewInternalClient>")
            .Append("<NewEnabled>1</NewEnabled>")
            .Append($"<NewPortMappingDescription>{SecurityElement.Escape(Description)}</NewPortMappingDescription>")
            .Append($"<NewLeaseDuration>{leaseSeconds}</NewLeaseDuration>")
            .ToString();
        var (ok, body) = await SoapAsync("AddPortMapping", args, cancellation).ConfigureAwait(false);
        if (!ok) LastError = ExtractSoapError(body) ?? "El router rechazó la apertura del puerto.";
        return ok;
    }

    private async Task<IPAddress?> GetExternalIpAsync(CancellationToken cancellation)
    {
        var (ok, body) = await SoapAsync("GetExternalIPAddress", string.Empty, cancellation).ConfigureAwait(false);
        if (!ok) return null;
        var doc = XDocument.Parse(body);
        var value = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "NewExternalIPAddress")?.Value;
        return IPAddress.TryParse(value, out var ip) ? ip : null;
    }

    private async Task<(bool Ok, string Body)> SoapAsync(string action, string arguments, CancellationToken cancellation)
    {
        var envelope =
            "<?xml version=\"1.0\"?>" +
            "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
            $"<s:Body><u:{action} xmlns:u=\"{_serviceType}\">{arguments}</u:{action}></s:Body></s:Envelope>";
        using var request = new HttpRequestMessage(HttpMethod.Post, _controlUrl)
        {
            Content = new StringContent(envelope, TextEncoding.UTF8, "text/xml"),
        };
        request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{_serviceType}#{action}\"");
        using var response = await _http.SendAsync(request, cancellation).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false);
        return (response.IsSuccessStatusCode, body);
    }

    private static string? ExtractSoapError(string body)
    {
        try
        {
            var doc = XDocument.Parse(body);
            var code = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "errorCode")?.Value;
            var text = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "errorDescription")?.Value;
            return code is null ? null : $"UPnP error {code}: {text}";
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (State == UpnpState.Mapped && _controlUrl is not null)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var args = $"<NewRemoteHost></NewRemoteHost><NewExternalPort>{_port}</NewExternalPort><NewProtocol>TCP</NewProtocol>";
                await SoapAsync("DeletePortMapping", args, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or SocketException or IOException)
            {
            }
        }
        _http.Dispose();
    }
}
