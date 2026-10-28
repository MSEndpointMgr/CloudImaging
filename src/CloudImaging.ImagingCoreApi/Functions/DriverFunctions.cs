using System.Net;
using System.Web;
using CloudImaging.Contracts.Enums;
using CloudImaging.Contracts.Models.Drivers;
using CloudImaging.ImagingCoreApi.Services.Drivers;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace CloudImaging.ImagingCoreApi.Functions;

/// <summary>
/// Driver model endpoints, called by the Operator API on behalf of the portal.
///
/// GET  internal/drivers/vendors                          vendors with Sync switch and model count
/// GET  internal/drivers/catalog/models?vendor=&amp;search=   vendor model list, onboarded ones flagged
/// POST internal/drivers/models/preview                   counts and size per model
/// POST internal/drivers/models                           onboard as drafts
/// GET  internal/drivers/models                           Models tab
/// GET  internal/drivers/models/{id}?release=             model page
/// POST internal/drivers/models/{id}/complete             Complete onboarding
/// POST internal/drivers/models/{id}/releases             add a Windows release
/// GET  internal/drivers/rules                            rules with hits
/// GET  internal/drivers/rules/suggestions                suggested rules not added yet
/// POST internal/drivers/rules                            add a rule
/// PUT  internal/drivers/rules/{id}                       change a rule
/// DELETE internal/drivers/rules/{id}?changedBy=          delete a rule
/// GET  internal/drivers/categories?vendor=               categories with the critical flag
/// PUT  internal/drivers/critical-categories/{vendor}     replace a vendor's critical categories
/// PUT|DELETE internal/drivers/models/{id}/releases/{release}/overrides/{key}   override and undo
/// POST internal/drivers/models/{id}/releases/{release}/build                   Build now
/// </summary>
public sealed class DriverFunctions
{
    private readonly DriverModelService _models;
    private readonly DriverPolicyService _policy;

    /// <param name="models">Onboarding and model pages.</param>
    /// <param name="policy">Rules, overrides and critical categories.</param>
    public DriverFunctions(DriverModelService models, DriverPolicyService policy)
    {
        _models = models;
        _policy = policy;
    }

    /// <summary>GET internal/drivers/vendors.</summary>
    [Function("GetDriverVendors")]
    public async Task<HttpResponseData> GetDriverVendors(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/drivers/vendors")] HttpRequestData req,
        FunctionContext context) =>
        await DriverHttp.JsonAsync(req, HttpStatusCode.OK, await _models.ListVendorsAsync(context.CancellationToken), context.CancellationToken);

    /// <summary>PUT internal/drivers/vendors/{vendor}.</summary>
    [Function("SetDriverVendor")]
    public async Task<HttpResponseData> SetDriverVendor(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "internal/drivers/vendors/{vendor}")] HttpRequestData req,
        string vendor,
        FunctionContext context)
    {
        if (!Enum.TryParse<DriverVendor>(vendor, true, out var parsed) || !Enum.IsDefined(parsed))
        {
            return await DriverHttp.ProblemAsync(req, HttpStatusCode.BadRequest, "Choose a vendor.", context.CancellationToken);
        }

        var request = await DriverHttp.ReadAsync<SetDriverVendorRequest>(req, context.CancellationToken);
        return await DriverHttp.ResultAsync(req, await _models.SetVendorAsync(parsed, request, context.CancellationToken), context.CancellationToken);
    }

    /// <summary>GET internal/drivers/catalog/models.</summary>
    [Function("GetDriverCatalogModels")]
    public async Task<HttpResponseData> GetDriverCatalogModels(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/drivers/catalog/models")] HttpRequestData req,
        FunctionContext context)
    {
        var query = HttpUtility.ParseQueryString(req.Url.Query);
        if (!Enum.TryParse<DriverVendor>(query["vendor"], true, out var vendor) || !Enum.IsDefined(vendor))
        {
            return await DriverHttp.ProblemAsync(req, HttpStatusCode.BadRequest, "Choose a vendor.", context.CancellationToken);
        }

        return await DriverHttp.ResultAsync(req, await _models.GetCatalogModelsAsync(vendor, query["search"], context.CancellationToken), context.CancellationToken);
    }

    /// <summary>POST internal/drivers/models/preview.</summary>
    [Function("PreviewDriverModels")]
    public async Task<HttpResponseData> PreviewDriverModels(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/drivers/models/preview")] HttpRequestData req,
        FunctionContext context)
    {
        var request = await DriverHttp.ReadAsync<DriverOnboardRequest>(req, context.CancellationToken);
        return request is null
            ? await DriverHttp.ProblemAsync(req, HttpStatusCode.BadRequest, DriverHttp.InvalidBody, context.CancellationToken)
            : await DriverHttp.ResultAsync(req, await _models.PreviewAsync(request, context.CancellationToken), context.CancellationToken);
    }

    /// <summary>POST internal/drivers/models.</summary>
    [Function("OnboardDriverModels")]
    public async Task<HttpResponseData> OnboardDriverModels(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/drivers/models")] HttpRequestData req,
        FunctionContext context)
    {
        var request = await DriverHttp.ReadAsync<DriverOnboardRequest>(req, context.CancellationToken);
        return request is null
            ? await DriverHttp.ProblemAsync(req, HttpStatusCode.BadRequest, DriverHttp.InvalidBody, context.CancellationToken)
            : await DriverHttp.ResultAsync(req, await _models.OnboardAsync(request, context.CancellationToken), context.CancellationToken);
    }

    /// <summary>GET internal/drivers/models.</summary>
    [Function("GetDriverModels")]
    public async Task<HttpResponseData> GetDriverModels(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/drivers/models")] HttpRequestData req,
        FunctionContext context) =>
        await DriverHttp.JsonAsync(req, HttpStatusCode.OK, await _models.ListModelsAsync(context.CancellationToken), context.CancellationToken);

    /// <summary>GET internal/drivers/models/{id}?release=.</summary>
    [Function("GetDriverModel")]
    public async Task<HttpResponseData> GetDriverModel(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/drivers/models/{id}")] HttpRequestData req,
        string id,
        FunctionContext context)
    {
        if (!Guid.TryParse(id, out var modelId))
        {
            return await DriverHttp.ProblemAsync(req, HttpStatusCode.BadRequest, "Invalid model id.", context.CancellationToken);
        }

        var releaseText = HttpUtility.ParseQueryString(req.Url.Query)["release"];
        var release = DriverModelService.ParseRelease(releaseText);
        if (releaseText is { Length: > 0 } && release is null)
        {
            return await DriverHttp.ProblemAsync(req, HttpStatusCode.BadRequest, "Unknown Windows release.", context.CancellationToken);
        }

        return await DriverHttp.ResultAsync(req, await _models.GetModelPageAsync(modelId, release, context.CancellationToken), context.CancellationToken);
    }

    /// <summary>POST internal/drivers/models/{id}/complete.</summary>
    [Function("CompleteDriverModelOnboarding")]
    public async Task<HttpResponseData> CompleteDriverModelOnboarding(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/drivers/models/{id}/complete")] HttpRequestData req,
        string id,
        FunctionContext context)
    {
        if (!Guid.TryParse(id, out var modelId))
        {
            return await DriverHttp.ProblemAsync(req, HttpStatusCode.BadRequest, "Invalid model id.", context.CancellationToken);
        }

        var request = await DriverHttp.ReadAsync<DriverActorRequest>(req, context.CancellationToken);
        return await DriverHttp.ResultAsync(req, await _models.CompleteAsync(modelId, request?.Actor, context.CancellationToken), context.CancellationToken);
    }

    /// <summary>POST internal/drivers/models/{id}/releases.</summary>
    [Function("AddDriverModelRelease")]
    public async Task<HttpResponseData> AddDriverModelRelease(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/drivers/models/{id}/releases")] HttpRequestData req,
        string id,
        FunctionContext context)
    {
        if (!Guid.TryParse(id, out var modelId))
        {
            return await DriverHttp.ProblemAsync(req, HttpStatusCode.BadRequest, "Invalid model id.", context.CancellationToken);
        }

        var request = await DriverHttp.ReadAsync<DriverAddReleaseRequest>(req, context.CancellationToken);
        return request is null
            ? await DriverHttp.ProblemAsync(req, HttpStatusCode.BadRequest, DriverHttp.InvalidBody, context.CancellationToken)
            : await DriverHttp.ResultAsync(req, await _models.AddReleaseAsync(modelId, request, context.CancellationToken), context.CancellationToken);
    }

    /// <summary>GET internal/drivers/rules.</summary>
    [Function("GetDriverRules")]
    public async Task<HttpResponseData> GetDriverRules(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/drivers/rules")] HttpRequestData req,
        FunctionContext context) =>
        await DriverHttp.JsonAsync(req, HttpStatusCode.OK, await _policy.ListRulesAsync(context.CancellationToken), context.CancellationToken);

    /// <summary>GET internal/drivers/rules/suggestions.</summary>
    [Function("GetDriverRuleSuggestions")]
    public async Task<HttpResponseData> GetDriverRuleSuggestions(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/drivers/rules/suggestions")] HttpRequestData req,
        FunctionContext context) =>
        await DriverHttp.JsonAsync(req, HttpStatusCode.OK, await _policy.SuggestionsAsync(context.CancellationToken), context.CancellationToken);

    /// <summary>POST internal/drivers/rules.</summary>
    [Function("CreateDriverRule")]
    public async Task<HttpResponseData> CreateDriverRule(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/drivers/rules")] HttpRequestData req,
        FunctionContext context) =>
        await DriverHttp.ResultAsync(req, await _policy.CreateRuleAsync(await DriverHttp.ReadAsync<DriverRuleRequest>(req, context.CancellationToken), context.CancellationToken), context.CancellationToken);

    /// <summary>PUT internal/drivers/rules/{id}.</summary>
    [Function("UpdateDriverRule")]
    public async Task<HttpResponseData> UpdateDriverRule(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "internal/drivers/rules/{id}")] HttpRequestData req,
        string id,
        FunctionContext context)
    {
        if (!Guid.TryParse(id, out var ruleId))
        {
            return await DriverHttp.ProblemAsync(req, HttpStatusCode.BadRequest, "Invalid rule id.", context.CancellationToken);
        }

        var request = await DriverHttp.ReadAsync<DriverRuleRequest>(req, context.CancellationToken);
        return await DriverHttp.ResultAsync(req, await _policy.UpdateRuleAsync(ruleId, request, context.CancellationToken), context.CancellationToken);
    }

    /// <summary>DELETE internal/drivers/rules/{id}?changedBy=.</summary>
    [Function("DeleteDriverRule")]
    public async Task<HttpResponseData> DeleteDriverRule(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "internal/drivers/rules/{id}")] HttpRequestData req,
        string id,
        FunctionContext context)
    {
        if (!Guid.TryParse(id, out var ruleId))
        {
            return await DriverHttp.ProblemAsync(req, HttpStatusCode.BadRequest, "Invalid rule id.", context.CancellationToken);
        }

        return await DriverHttp.ResultAsync(req, await _policy.DeleteRuleAsync(ruleId, DriverHttp.ChangedBy(req), context.CancellationToken), context.CancellationToken);
    }

    /// <summary>GET internal/drivers/categories?vendor=.</summary>
    [Function("GetDriverCategories")]
    public async Task<HttpResponseData> GetDriverCategories(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "internal/drivers/categories")] HttpRequestData req,
        FunctionContext context)
    {
        var vendorText = HttpUtility.ParseQueryString(req.Url.Query)["vendor"];
        DriverVendor? vendor = null;
        if (vendorText is { Length: > 0 })
        {
            if (!Enum.TryParse<DriverVendor>(vendorText, true, out var parsed) || !Enum.IsDefined(parsed))
            {
                return await DriverHttp.ProblemAsync(req, HttpStatusCode.BadRequest, "Choose a vendor.", context.CancellationToken);
            }

            vendor = parsed;
        }

        return await DriverHttp.JsonAsync(req, HttpStatusCode.OK, await _policy.ListCategoriesAsync(vendor, context.CancellationToken), context.CancellationToken);
    }

    /// <summary>PUT internal/drivers/critical-categories/{vendor}.</summary>
    [Function("SetDriverCriticalCategories")]
    public async Task<HttpResponseData> SetDriverCriticalCategories(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "internal/drivers/critical-categories/{vendor}")] HttpRequestData req,
        string vendor,
        FunctionContext context)
    {
        if (!Enum.TryParse<DriverVendor>(vendor, true, out var parsed) || !Enum.IsDefined(parsed))
        {
            return await DriverHttp.ProblemAsync(req, HttpStatusCode.BadRequest, "Choose a vendor.", context.CancellationToken);
        }

        var request = await DriverHttp.ReadAsync<DriverCriticalCategoriesRequest>(req, context.CancellationToken);
        return await DriverHttp.ResultAsync(req, await _policy.SetCriticalCategoriesAsync(parsed, request, context.CancellationToken), context.CancellationToken);
    }

    /// <summary>PUT internal/drivers/models/{id}/releases/{release}/overrides/{key}.</summary>
    [Function("SetDriverOverride")]
    public async Task<HttpResponseData> SetDriverOverride(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "internal/drivers/models/{id}/releases/{release}/overrides/{key}")] HttpRequestData req,
        string id,
        string release,
        string key,
        FunctionContext context)
    {
        if (await DriverHttp.PairAsync(req, id, release, key, context.CancellationToken) is { Problem: { } problem })
        {
            return problem;
        }

        var request = await DriverHttp.ReadAsync<DriverOverrideRequest>(req, context.CancellationToken);
        return await DriverHttp.ResultAsync(req, await _policy.SetOverrideAsync(Guid.Parse(id), DriverModelService.ParseRelease(release)!.Value, key, request, context.CancellationToken), context.CancellationToken);
    }

    /// <summary>DELETE internal/drivers/models/{id}/releases/{release}/overrides/{key}?changedBy=.</summary>
    [Function("ClearDriverOverride")]
    public async Task<HttpResponseData> ClearDriverOverride(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "internal/drivers/models/{id}/releases/{release}/overrides/{key}")] HttpRequestData req,
        string id,
        string release,
        string key,
        FunctionContext context)
    {
        if (await DriverHttp.PairAsync(req, id, release, key, context.CancellationToken) is { Problem: { } problem })
        {
            return problem;
        }

        return await DriverHttp.ResultAsync(req, await _policy.ClearOverrideAsync(Guid.Parse(id), DriverModelService.ParseRelease(release)!.Value, key, DriverHttp.ChangedBy(req), context.CancellationToken), context.CancellationToken);
    }

    /// <summary>POST internal/drivers/models/{id}/releases/{release}/approvals.</summary>
    [Function("ApproveDriverComponents")]
    public async Task<HttpResponseData> ApproveDriverComponents(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/drivers/models/{id}/releases/{release}/approvals")] HttpRequestData req,
        string id,
        string release,
        FunctionContext context)
    {
        if (await DriverHttp.PairAsync(req, id, release, null, context.CancellationToken) is { Problem: { } problem })
        {
            return problem;
        }

        var request = await DriverHttp.ReadAsync<DriverApprovalRequest>(req, context.CancellationToken);
        return await DriverHttp.ResultAsync(req, await _policy.ApproveAsync(Guid.Parse(id), DriverModelService.ParseRelease(release)!.Value, request, context.CancellationToken), context.CancellationToken);
    }

    /// <summary>POST internal/drivers/models/{id}/releases/{release}/build: Build now.</summary>
    [Function("BuildDriverSetNow")]
    public async Task<HttpResponseData> BuildDriverSetNow(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "internal/drivers/models/{id}/releases/{release}/build")] HttpRequestData req,
        string id,
        string release,
        FunctionContext context)
    {
        if (await DriverHttp.PairAsync(req, id, release, null, context.CancellationToken) is { Problem: { } problem })
        {
            return problem;
        }

        var request = await DriverHttp.ReadAsync<DriverActorRequest>(req, context.CancellationToken);
        return await DriverHttp.ResultAsync(req, await _policy.BuildNowAsync(Guid.Parse(id), DriverModelService.ParseRelease(release)!.Value, request?.Actor, context.CancellationToken), context.CancellationToken);
    }
}
