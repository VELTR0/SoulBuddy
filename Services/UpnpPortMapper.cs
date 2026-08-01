using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace SoulBuddy.Services;

internal sealed class UpnpPortMapper : IDisposable
{
    private static readonly IPEndPoint SsdpEndpoint =
        new(IPAddress.Parse("239.255.255.250"), 1900);

    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(4)
    };

    private UpnpService? _service;

    public async Task<PortMappingResult> TryCreateMappingAsync(
        int port,
        CancellationToken cancellationToken)
    {
        _service ??= await DiscoverServiceAsync(cancellationToken);
        if (_service is null)
        {
            return new PortMappingResult(false, null);
        }

        var localAddress = GetLocalAddressFor(_service.ControlUri);
        if (localAddress is null)
        {
            return new PortMappingResult(false, null);
        }

        await InvokeAsync(
            _service,
            "AddPortMapping",
            new Dictionary<string, string>
            {
                ["NewRemoteHost"] = string.Empty,
                ["NewExternalPort"] = port.ToString(),
                ["NewProtocol"] = "TCP",
                ["NewInternalPort"] = port.ToString(),
                ["NewInternalClient"] = localAddress.ToString(),
                ["NewEnabled"] = "1",
                ["NewPortMappingDescription"] = "SoulBuddy",
                ["NewLeaseDuration"] = "0"
            },
            cancellationToken);

        var externalIp = await GetExternalIpAsync(_service, cancellationToken);
        var address = string.IsNullOrWhiteSpace(externalIp)
            ? null
            : $"{externalIp}:{port}";
        return new PortMappingResult(true, address);
    }

    public async Task TryDeleteMappingAsync(
        int port,
        CancellationToken cancellationToken = default)
    {
        if (_service is null)
        {
            return;
        }

        await InvokeAsync(
            _service,
            "DeletePortMapping",
            new Dictionary<string, string>
            {
                ["NewRemoteHost"] = string.Empty,
                ["NewExternalPort"] = port.ToString(),
                ["NewProtocol"] = "TCP"
            },
            cancellationToken);
    }

    private async Task<string?> GetExternalIpAsync(
        UpnpService service,
        CancellationToken cancellationToken)
    {
        var response = await InvokeAsync(
            service,
            "GetExternalIPAddress",
            new Dictionary<string, string>(),
            cancellationToken);

        try
        {
            var document = XDocument.Parse(response);
            return document
                .Descendants()
                .FirstOrDefault(element =>
                    element.Name.LocalName == "NewExternalIPAddress")
                ?.Value
                .Trim();
        }
        catch
        {
            return null;
        }
    }

    private async Task<UpnpService?> DiscoverServiceAsync(
        CancellationToken cancellationToken)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.ReceiveTimeout = 2500;

        var request = string.Join("\r\n",
            "M-SEARCH * HTTP/1.1",
            "HOST: 239.255.255.250:1900",
            "MAN: \"ssdp:discover\"",
            "MX: 2",
            "ST: urn:schemas-upnp-org:device:InternetGatewayDevice:1",
            string.Empty,
            string.Empty);
        var bytes = Encoding.ASCII.GetBytes(request);
        await udp.SendAsync(bytes, SsdpEndpoint, cancellationToken);

        using var timeoutSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(3));

        while (!timeoutSource.IsCancellationRequested)
        {
            UdpReceiveResult response;
            try
            {
                response = await udp.ReceiveAsync(timeoutSource.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }

            var text = Encoding.UTF8.GetString(response.Buffer);
            var location = ReadHeader(text, "LOCATION");
            if (!Uri.TryCreate(location, UriKind.Absolute, out var descriptionUri))
            {
                continue;
            }

            var service = await ReadServiceAsync(
                descriptionUri,
                timeoutSource.Token);
            if (service is not null)
            {
                return service;
            }
        }

        return null;
    }

    private async Task<UpnpService?> ReadServiceAsync(
        Uri descriptionUri,
        CancellationToken cancellationToken)
    {
        var xml = await _httpClient.GetStringAsync(
            descriptionUri,
            cancellationToken);
        var document = XDocument.Parse(xml);

        foreach (var service in document.Descendants()
                     .Where(element => element.Name.LocalName == "service"))
        {
            var serviceType = service.Elements()
                .FirstOrDefault(element =>
                    element.Name.LocalName == "serviceType")
                ?.Value;
            if (serviceType is null ||
                (!serviceType.Contains("WANIPConnection", StringComparison.OrdinalIgnoreCase) &&
                 !serviceType.Contains("WANPPPConnection", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var controlUrl = service.Elements()
                .FirstOrDefault(element =>
                    element.Name.LocalName == "controlURL")
                ?.Value;
            if (string.IsNullOrWhiteSpace(controlUrl))
            {
                continue;
            }

            return new UpnpService(
                serviceType,
                new Uri(descriptionUri, controlUrl));
        }

        return null;
    }

    private async Task<string> InvokeAsync(
        UpnpService service,
        string action,
        IReadOnlyDictionary<string, string> arguments,
        CancellationToken cancellationToken)
    {
        var body = new StringBuilder();
        body.Append("<?xml version=\"1.0\"?>")
            .Append("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" ")
            .Append("s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">")
            .Append("<s:Body>")
            .Append("<u:").Append(action).Append(" xmlns:u=\"")
            .Append(service.ServiceType).Append("\">");

        foreach (var argument in arguments)
        {
            body.Append('<').Append(argument.Key).Append('>')
                .Append(System.Security.SecurityElement.Escape(argument.Value))
                .Append("</").Append(argument.Key).Append('>');
        }

        body.Append("</u:").Append(action).Append('>')
            .Append("</s:Body></s:Envelope>");

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            service.ControlUri);
        request.Headers.TryAddWithoutValidation(
            "SOAPACTION",
            $"\"{service.ServiceType}#{action}\"");
        request.Content = new StringContent(
            body.ToString(),
            Encoding.UTF8,
            "text/xml");
        request.Content.Headers.ContentType =
            new MediaTypeHeaderValue("text/xml") { CharSet = "utf-8" };

        using var response = await _httpClient.SendAsync(
            request,
            cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return responseBody;
    }

    private static IPAddress? GetLocalAddressFor(Uri destination)
    {
        try
        {
            using var socket = new Socket(
                AddressFamily.InterNetwork,
                SocketType.Dgram,
                ProtocolType.Udp);
            socket.Connect(destination.Host, destination.Port);
            return (socket.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadHeader(string response, string name)
    {
        foreach (var line in response.Split("\r\n"))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0 ||
                !line[..separator].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return line[(separator + 1)..].Trim();
        }

        return null;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private sealed record UpnpService(string ServiceType, Uri ControlUri);
}

internal sealed record PortMappingResult(
    bool Success,
    string? ExternalAddress);
