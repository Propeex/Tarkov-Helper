using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;

internal sealed class TarkovApiOutageFixtureHandler : HttpMessageHandler
{
    public int StaticRequestCount { get; private set; }
    public int GraphQlRequestCount { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var host = request.RequestUri?.Host ?? string.Empty;

        if (string.Equals(host, "json.tarkov.dev", StringComparison.OrdinalIgnoreCase))
        {
            StaticRequestCount++;
            throw new HttpRequestException(
                "No such host is known.",
                new SocketException((int)SocketError.HostNotFound));
        }

        if (string.Equals(host, "api.tarkov.dev", StringComparison.OrdinalIgnoreCase))
        {
            GraphQlRequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                RequestMessage = request,
                Content = new StringContent(
                    "{\"errors\":[\"GraphQL server unavailable. Try again later.\"]}",
                    Encoding.UTF8,
                    "application/json")
            });
        }

        throw new InvalidOperationException($"Unexpected outage fixture request: {request.RequestUri}");
    }
}
