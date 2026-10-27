using System.Net;
using CloudImaging.Contracts.Models.Drivers;
using CloudImaging.ImagingCoreApi.Services.Drivers;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace CloudImaging.ImagingCoreApi.Functions;

/// <summary>
/// Custom components, called by the Operator API on behalf of the portal.
///
/// POST   internal/drivers/custom-components/uploads/start               { fileName, sizeBytes }
/// POST   internal/drivers/custom-components/uploads/{id}/complete       { blockIds }
/// POST   internal/drivers/custom-components/uploads/{id}/abandon
/// POST   internal/drivers/custom-components                             add
/// GET    internal/drivers/custom-components                             list
/// PUT    internal/drivers/custom-components/{id}                        change
/// DELETE internal/drivers/custom-components/{id}?changedBy=             remove
/// POST   internal/drivers/custom-components/{id}/notices/{modelId}/dismiss
/// </summary>
public sealed class DriverCustomComponentFunctions
{
    private readonly DriverCustomComponentService _custom;

    /// <param name="custom">Custom components.</param>
    public DriverCustomComponentFunctions(DriverCustomComponentService custom) => _custom = custom;

    /// <summary>POST internal/drivers/custom-components/uploads/start.</summary>
    [Function("StartCustomComponentUpload")]
    public async Task<HttpResponseData> StartCustomComponentUpload(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/drivers/custom-components/uploads/start")] HttpRequestData req,
        FunctionContext context) =>
        await DriverHttp.ResultAsync(req, await _custom.StartUploadAsync(await DriverHttp.ReadAsync<StartDriverCustomUploadRequest>(req, context.CancellationToken), context.CancellationToken), context.CancellationToken);

    /// <summary>POST internal/drivers/custom-components/uploads/{id}/complete.</summary>
    [Function("CompleteCustomComponentUpload")]
    public async Task<HttpResponseData> CompleteCustomComponentUpload(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/drivers/custom-components/uploads/{id}/complete")] HttpRequestData req,
        string id,
        FunctionContext context) =>
        Guid.TryParse(id, out var uploadId)
            ? await DriverHttp.ResultAsync(req, await _custom.CompleteUploadAsync(uploadId, await DriverHttp.ReadAsync<CompleteDriverCustomUploadRequest>(req, context.CancellationToken), context.CancellationToken), context.CancellationToken)
            : await DriverHttp.ProblemAsync(req, HttpStatusCode.BadRequest, "Invalid upload id.", context.CancellationToken);

    /// <summary>POST internal/drivers/custom-components/uploads/{id}/abandon.</summary>
    [Function("AbandonCustomComponentUpload")]
    public async Task<HttpResponseData> AbandonCustomComponentUpload(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/drivers/custom-components/uploads/{id}/abandon")] HttpRequestData req,
        string id,
        FunctionContext context) =>
        Guid.TryParse(id, out var uploadId)
            ? await DriverHttp.ResultAsync(req, await _custom.AbandonUploadAsync(uploadId, (await DriverHttp.ReadAsync<DriverActorRequest>(req, context.CancellationToken))?.Actor?.Upn, context.CancellationToken), context.CancellationToken)
            : await DriverHttp.ProblemAsync(req, HttpStatusCode.BadRequest, "Invalid upload id.", context.CancellationToken);

    /// <summary>POST internal/drivers/custom-components.</summary>
    [Function("CreateCustomComponent")]
    public async Task<HttpResponseData> CreateCustomComponent(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/drivers/custom-components")] HttpRequestData req,
        FunctionContext context) =>
        await DriverHttp.ResultAsync(req, await _custom.CreateAsync(await DriverHttp.ReadAsync<DriverCustomComponentRequest>(req, context.CancellationToken), context.CancellationToken), context.CancellationToken);

    /// <summary>GET internal/drivers/custom-components.</summary>
    [Function("GetCustomComponents")]
    public async Task<HttpResponseData> GetCustomComponents(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/drivers/custom-components")] HttpRequestData req,
        FunctionContext context) =>
        await DriverHttp.JsonAsync(req, HttpStatusCode.OK, await _custom.ListAsync(context.CancellationToken), context.CancellationToken);

    /// <summary>PUT internal/drivers/custom-components/{id}.</summary>
    [Function("UpdateCustomComponent")]
    public async Task<HttpResponseData> UpdateCustomComponent(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "internal/drivers/custom-components/{id}")] HttpRequestData req,
        string id,
        FunctionContext context) =>
        Guid.TryParse(id, out var customId)
            ? await DriverHttp.ResultAsync(req, await _custom.UpdateAsync(customId, await DriverHttp.ReadAsync<DriverCustomComponentRequest>(req, context.CancellationToken), context.CancellationToken), context.CancellationToken)
            : await DriverHttp.ProblemAsync(req, HttpStatusCode.BadRequest, "Invalid custom component id.", context.CancellationToken);

    /// <summary>DELETE internal/drivers/custom-components/{id}?changedBy=.</summary>
    [Function("DeleteCustomComponent")]
    public async Task<HttpResponseData> DeleteCustomComponent(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "internal/drivers/custom-components/{id}")] HttpRequestData req,
        string id,
        FunctionContext context) =>
        Guid.TryParse(id, out var customId)
            ? await DriverHttp.ResultAsync(req, await _custom.DeleteAsync(customId, DriverHttp.ChangedBy(req), context.CancellationToken), context.CancellationToken)
            : await DriverHttp.ProblemAsync(req, HttpStatusCode.BadRequest, "Invalid custom component id.", context.CancellationToken);

    /// <summary>POST internal/drivers/custom-components/{id}/notices/{modelId}/dismiss.</summary>
    [Function("DismissCustomComponentNotice")]
    public async Task<HttpResponseData> DismissCustomComponentNotice(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/drivers/custom-components/{id}/notices/{modelId}/dismiss")] HttpRequestData req,
        string id,
        string modelId,
        FunctionContext context)
    {
        if (!Guid.TryParse(id, out var customId) || !Guid.TryParse(modelId, out var model))
        {
            return await DriverHttp.ProblemAsync(req, HttpStatusCode.BadRequest, "Invalid id.", context.CancellationToken);
        }

        var request = await DriverHttp.ReadAsync<DriverActorRequest>(req, context.CancellationToken);
        return await DriverHttp.ResultAsync(req, await _custom.DismissAsync(customId, model, request?.Actor, context.CancellationToken), context.CancellationToken);
    }
}
