using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Moq;

namespace CloudImaging.DeviceGatewayApi.Tests.TestDoubles;

/// <summary>Minimal HTTP doubles for calling isolated-worker functions directly and faking Imaging Core.</summary>
internal static class FunctionHttp
{
    public static FunctionContext Context(IDictionary<object, object>? items = null)
    {
        var context = new Mock<FunctionContext>();
        context.SetupGet(c => c.Items).Returns(items ?? new Dictionary<object, object>());
        return context.Object;
    }

    public static TestRequest Request(FunctionContext context, string method, string url, string body = "") =>
        new(context, method, new Uri(url), body);

    public static async Task<string> ReadBodyAsync(this HttpResponseData response)
    {
        response.Body.Position = 0;
        using var reader = new StreamReader(response.Body, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }
}

internal sealed class TestRequest(FunctionContext context, string method, Uri url, string body) : HttpRequestData(context)
{
    public override Stream Body { get; } = new MemoryStream(Encoding.UTF8.GetBytes(body));

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

internal sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request, body));
        return respond(request);
    }
}
