using System.Net;
using System.Text;
using System.Text.Json;

namespace Oficina.WebhookForwarder;

public sealed class Function
{
    private const string ExpectedPath = "/webhooks/mercado-pago/orders";
    private static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly HttpClient _client;
    private readonly string _billingEndpoint;

    public Function() : this(SharedClient, Required("BILLING_WEBHOOK_URL")) { }

    public Function(HttpClient client, string billingEndpoint)
    {
        _client = client;
        _billingEndpoint = billingEndpoint.TrimEnd('/');
    }

    public async Task<Stream> FunctionHandler(Stream input)
    {
        try
        {
            using var gatewayEvent = await JsonDocument.ParseAsync(input);
            var root = gatewayEvent.RootElement;
            var path = GetString(root, "rawPath");
            var method = root.TryGetProperty("requestContext", out var requestContext) &&
                requestContext.TryGetProperty("http", out var http) ? GetString(http, "method") : null;
            if (path != ExpectedPath) return Response(HttpStatusCode.NotFound);
            if (!string.Equals(method, "POST", StringComparison.Ordinal))
                return Response(HttpStatusCode.MethodNotAllowed);

            var rawQuery = GetString(root, "rawQueryString") ?? string.Empty;
            var body = GetString(root, "body") ?? string.Empty;
            var isBase64 = root.TryGetProperty("isBase64Encoded", out var encoded) &&
                encoded.ValueKind == JsonValueKind.True;
            var bodyBytes = isBase64 ? Convert.FromBase64String(body) : Encoding.UTF8.GetBytes(body);
            var destination = rawQuery.Length == 0 ? _billingEndpoint : _billingEndpoint + "?" + rawQuery;
            using var request = new HttpRequestMessage(HttpMethod.Post, destination)
            {
                Content = new ByteArrayContent(bodyBytes)
            };
            if (TryGetHeader(root, "content-type", out var contentType))
                request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            if (TryGetHeader(root, "x-signature", out var signature))
                request.Headers.TryAddWithoutValidation("x-signature", signature);
            if (TryGetHeader(root, "x-request-id", out var requestId))
                request.Headers.TryAddWithoutValidation("x-request-id", requestId);

            using var upstream = await _client.SendAsync(request);
            var responseBytes = await upstream.Content.ReadAsByteArrayAsync();
            var responseText = Encoding.UTF8.GetString(responseBytes);
            var responseContentType = upstream.Content.Headers.ContentType?.ToString();
            return Response((HttpStatusCode)upstream.StatusCode, responseText, responseContentType);
        }
        catch (OperationCanceledException)
        {
            return Response(HttpStatusCode.GatewayTimeout);
        }
        catch (HttpRequestException)
        {
            return Response(HttpStatusCode.BadGateway);
        }
        catch (JsonException)
        {
            return Response(HttpStatusCode.BadRequest);
        }
        catch (FormatException)
        {
            return Response(HttpStatusCode.BadRequest);
        }
    }

    private static bool TryGetHeader(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty("headers", out var headers) || headers.ValueKind != JsonValueKind.Object)
            return false;
        foreach (var header in headers.EnumerateObject())
        {
            if (!header.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                header.Value.ValueKind != JsonValueKind.String) continue;
            value = header.Value.GetString() ?? string.Empty;
            return value.Length > 0;
        }
        return false;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static Stream Response(HttpStatusCode status, string body = "", string? contentType = null)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            statusCode = (int)status,
            headers = contentType is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string> { ["content-type"] = contentType },
            body,
            isBase64Encoded = false
        });
        return new MemoryStream(payload);
    }

    private static string Required(string name) => Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"{name} não configurado.");
}
