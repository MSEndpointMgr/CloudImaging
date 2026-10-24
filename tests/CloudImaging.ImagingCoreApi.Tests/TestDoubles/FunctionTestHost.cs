using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Moq;

namespace CloudImaging.ImagingCoreApi.Tests.TestDoubles;

/// <summary>Minimal HTTP request and response doubles for calling isolated-worker functions directly.</summary>
internal static class FunctionTestHost
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static FunctionContext Context { get; } = new Mock<FunctionContext>().Object;

    public static TestRequest Request(string method, string url, object? body = null)
    {
        var json = body switch
        {
            null => string.Empty,
            string text => text,
            _ => JsonSerializer.Serialize(body, JsonOptions),
        };
        return new TestRequest(Context, method, new Uri(url), Encoding.UTF8.GetBytes(json));
    }

    public static TestRequest RawRequest(string method, string url, byte[] body) => new(Context, method, new Uri(url), body);

    public static async Task<string> ReadBodyAsync(this HttpResponseData response)
    {
        response.Body.Position = 0;
        using var reader = new StreamReader(response.Body, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    public static async Task<JsonElement> ReadJsonAsync(this HttpResponseData response)
    {
        var text = await response.ReadBodyAsync();
        return JsonDocument.Parse(text).RootElement.Clone();
    }
}

internal sealed class TestRequest(FunctionContext context, string method, Uri url, byte[] body) : HttpRequestData(context)
{
    public override Stream Body { get; } = new MemoryStream(body);

    public override HttpHeadersCollection Headers { get; } = [];

    public override IReadOnlyCollection<IHttpCookie> Cookies { get; } = [];

    public override Uri Url { get; } = url;

    public override IEnumerable<ClaimsIdentity> Identities { get; } = [];

    public override string Method { get; } = method;

    public override HttpResponseData CreateResponse() => new TestResponse(FunctionContext);
}

internal sealed class TestResponse(FunctionContext context) : HttpResponseData(context)
{
    public override HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;

    public override HttpHeadersCollection Headers { get; set; } = [];

    public override Stream Body { get; set; } = new MemoryStream();

    public override HttpCookies Cookies { get; } = new Mock<HttpCookies>().Object;
}
