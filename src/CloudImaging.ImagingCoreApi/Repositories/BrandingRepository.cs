using Azure;
using Azure.Data.Tables;
using CloudImaging.Contracts.Models;

namespace CloudImaging.ImagingCoreApi.Repositories;

/// <summary>
/// Table Storage repository for portal branding configuration (FR-038, FR-039).
/// PartitionKey: "branding" | RowKey: "default".
/// </summary>
public sealed class BrandingRepository
{
    private const string TableName = "BrandingConfiguration";
    private const string Partition = "branding";
    private const string Key = "default";
    private readonly TableClient _table;

    public BrandingRepository(TableServiceClient tableServiceClient) =>
        _table = tableServiceClient.GetTableClient(TableName);

    public async Task EnsureTableExistsAsync(CancellationToken ct = default) =>
        await _table.CreateIfNotExistsAsync(ct);

    public async Task<BrandingConfiguration> GetAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _table.GetEntityAsync<TableEntity>(Partition, Key, cancellationToken: ct);
            return FromEntity(response.Value);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return new BrandingConfiguration(); // Return defaults if not yet configured
        }
    }

    public async Task UpsertAsync(BrandingConfiguration branding, CancellationToken ct = default)
    {
        var entity = new TableEntity(Partition, Key)
        {
            ["LogoBlobPath"]    = branding.LogoBlobPath,
            ["PrimaryColor"]    = branding.PrimaryColor,
            ["AccentColor"]     = branding.AccentColor,
            ["ApplicationName"] = branding.ApplicationName,
        };
        await _table.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);
    }

    private static BrandingConfiguration FromEntity(TableEntity e) => new()
    {
        LogoBlobPath    = e.GetString("LogoBlobPath"),
        PrimaryColor    = e.GetString("PrimaryColor") ?? "#0078d4",
        AccentColor     = e.GetString("AccentColor") ?? "#005a9e",
        ApplicationName = e.GetString("ApplicationName") ?? "Cloud Imaging",
    };
}
