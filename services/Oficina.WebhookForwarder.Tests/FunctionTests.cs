using System.Net;
using System.Text;
using System.Text.Json;
using Oficina.WebhookForwarder;
using Xunit;

namespace Oficina.WebhookForwarder.Tests;

public sealed class FunctionTests
{
    [Fact]
    public async Task ForwardsRawBodySignatureRequestIdAndRawQuery()
    {
        const string body = "{\"type\":\"order\",\"data\":{\"id\":\"ORDTST0123\"}}";
        var handler = new CaptureHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var function = new Function(new HttpClient(handler), "http://10.42.0.77:30081/webhooks/mercado-pago/orders");

        using var response = JsonDocument.Parse(await Invoke(function, GatewayEvent(body,
            "data.id=ORDTST0123&source_news=webhooks", "ts=1700000000,v1=abc", "req-123")));

        Assert.Equal("http://10.42.0.77:30081/webhooks/mercado-pago/orders?data.id=ORDTST0123&source_news=webhooks",
            handler.Request!.RequestUri!.AbsoluteUri);
        Assert.Equal(body, handler.Body);
        Assert.Equal("ts=1700000000,v1=abc", handler.Request.Headers.GetValues("x-signature").Single());
        Assert.Equal("req-123", handler.Request.Headers.GetValues("x-request-id").Single());
        Assert.Equal(200, response.RootElement.GetProperty("statusCode").GetInt32());
    }

    [Fact]
    public async Task PreservesBase64BodyAndUpstreamUnauthorizedStatus()
    {
        const string body = "{\"type\":\"order\"}";
        var handler = new CaptureHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var function = new Function(new HttpClient(handler), "http://10.42.0.77:30081/webhooks/mercado-pago/orders");

        using var response = JsonDocument.Parse(await Invoke(function,
            GatewayEvent(Convert.ToBase64String(Encoding.UTF8.GetBytes(body)), "data.id=ORDTST0123", "sig", "req",
                base64Encoded: true)));

        Assert.Equal(body, handler.Body);
        Assert.Equal(401, response.RootElement.GetProperty("statusCode").GetInt32());
    }

    [Fact]
    public async Task RejectsUnexpectedPathWithoutForwarding()
    {
        var handler = new CaptureHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var function = new Function(new HttpClient(handler), "http://10.42.0.77:30081/webhooks/mercado-pago/orders");

        using var response = JsonDocument.Parse(await Invoke(function,
            GatewayEvent("{}", "", "", "", path: "/other")));

        Assert.Equal(404, response.RootElement.GetProperty("statusCode").GetInt32());
        Assert.Null(handler.Request);
    }

    private static async Task<byte[]> Invoke(Function function, string json)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(json));
        using var output = await function.FunctionHandler(input);
        using var result = new MemoryStream();
        await output.CopyToAsync(result);
        return result.ToArray();
    }

    private static string GatewayEvent(string body, string query, string signature, string requestId,
        bool base64Encoded = false, string path = "/webhooks/mercado-pago/orders") => JsonSerializer.Serialize(new
    {
        rawPath = path,
        rawQueryString = query,
        headers = new Dictionary<string, string>
        {
            ["content-type"] = "application/json",
            ["x-signature"] = signature,
            ["x-request-id"] = requestId
        },
        body,
        isBase64Encoded = base64Encoded,
        requestContext = new { http = new { method = "POST" } }
    });

    private sealed class CaptureHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return responder(request);
        }
    }
}
