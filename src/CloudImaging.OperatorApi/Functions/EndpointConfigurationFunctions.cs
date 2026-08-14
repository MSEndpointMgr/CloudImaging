using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CloudImaging.OperatorApi.Functions;

/// <summary>
/// Exposes environment-level endpoint configuration that the Media Builder needs when
/// generating boot media — currently just the Device Gateway API base URL.
///
/// GET /api/configuration/endpoints — accessible by CloudImaging.MediaBuilderAccess (and
/// CloudImaging.PortalAccess). Values come straight from this deployment's own app settings
/// (wired from the Device Gateway API's Bicep output), so the Media Builder can stamp the
/// live URL into the Cloud Imaging Client's appsettings.json at boot-image build time instead
/// of relying on a manually maintained config file. This also self-heals across Device Gateway
/// redeploys/renames — every new boot image build picks up whatever URL is live right now.
/// </summary>
public sealed partial class EndpointConfigurationFunctions
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _deviceGatewayApiBaseUrl;
    private readonly ILogger<EndpointConfigurationFunctions> _logger;

    public EndpointConfigurationFunctions(IConfiguration configuration, ILogger<EndpointConfigurationFunctions> logger)
    {
        _deviceGatewayApiBaseUrl = configuration["DeviceGatewayApi:BaseUrl"]
            ?? configuration["DeviceGatewayApi__BaseUrl"]
            ?? throw new InvalidOperationException("DeviceGatewayApi__BaseUrl is not configured.");
        _logger = logger;
    }

    [Function(nameof(GetEndpointConfiguration))]
    public async Task<HttpResponseData> GetEndpointConfiguration(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "configuration/endpoints")] HttpRequestData req,
        FunctionContext context)
    {
        LogEndpointConfigurationRequested(_logger);
        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json");
        await response.WriteStringAsync(
            JsonSerializer.Serialize(new { deviceGatewayApiBaseUrl = _deviceGatewayApiBaseUrl }, JsonOptions),
            context.CancellationToken);
        return response;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Endpoint configuration requested (Device Gateway URL lookup).")]
    private static partial void LogEndpointConfigurationRequested(ILogger logger);
}
