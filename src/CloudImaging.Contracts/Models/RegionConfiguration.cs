namespace CloudImaging.Contracts.Models;

/// <summary>
/// A tenant region such as EMEA. <see cref="RegionId"/> is stable, so renaming <see cref="Code"/>
/// carries every country mapped to it along.
/// </summary>
public sealed class RegionDefinition
{
    /// <summary>Stable identifier. The built-in regions use "emea", "amer" and "apac".</summary>
    public required string RegionId { get; init; }

    /// <summary>Code emitted for the <c>{Region}</c> group tag token, e.g. EMEA.</summary>
    public required string Code { get; init; }
}

/// <summary>
/// Tenant-wide country-to-region mapping used to derive a location's region from its country.
/// Countries follow <see cref="CountryRegionDefaults"/> unless listed in <see cref="CountryOverrides"/>,
/// where a null value means the country has no region.
/// </summary>
public sealed class RegionConfiguration
{
    /// <summary>Maximum number of regions a tenant can define.</summary>
    public const int MaxRegions = 50;

    /// <summary>Maximum length of a region code.</summary>
    public const int MaxCodeLength = 16;

    /// <summary>The tenant's regions.</summary>
    public IReadOnlyList<RegionDefinition> Regions { get; init; } = [];

    /// <summary>Countries (ISO-3166 alpha-2) whose region differs from the built-in default.</summary>
    public IReadOnlyDictionary<string, string?> CountryOverrides { get; init; } = new Dictionary<string, string?>();

    /// <summary>When the configuration was last saved; default when it never has been.</summary>
    public DateTimeOffset LastModifiedAt { get; init; }

    /// <summary>EMEA, AMER and APAC with the built-in country map and no overrides.</summary>
    public static RegionConfiguration Default { get; } = new()
    {
        Regions =
        [
            new RegionDefinition { RegionId = CountryRegionDefaults.EmeaRegionId, Code = "EMEA" },
            new RegionDefinition { RegionId = CountryRegionDefaults.AmerRegionId, Code = "AMER" },
            new RegionDefinition { RegionId = CountryRegionDefaults.ApacRegionId, Code = "APAC" },
        ],
    };

    /// <summary>Region id a country is mapped to, or null when it has none.</summary>
    public string? RegionIdFor(string? countryCode)
    {
        if (string.IsNullOrWhiteSpace(countryCode))
        {
            return null;
        }

        var code = countryCode.Trim().ToUpperInvariant();
        var regionId = CountryOverrides.TryGetValue(code, out var overridden) ? overridden : CountryRegionDefaults.RegionIdFor(code);
        return regionId is not null && Regions.Any(r => r.RegionId == regionId) ? regionId : null;
    }

    /// <summary>Region code a country is mapped to, or null when it has none.</summary>
    public string? RegionCodeFor(string? countryCode)
    {
        var regionId = RegionIdFor(countryCode);
        return regionId is null ? null : Regions.First(r => r.RegionId == regionId).Code;
    }

    /// <summary>
    /// The region a location resolves to. A region typed before regions came from countries wins
    /// until the location is saved again, which clears it.
    /// </summary>
    public string? EffectiveRegion(Location location) =>
        string.IsNullOrWhiteSpace(location.Region) ? RegionCodeFor(location.CountryCode) : location.Region;

    /// <summary>Returns a copy with trimmed, upper-cased codes and country keys.</summary>
    public RegionConfiguration Normalized() => new()
    {
        Regions = Regions.Select(r => new RegionDefinition { RegionId = r.RegionId?.Trim() ?? string.Empty, Code = r.Code?.Trim().ToUpperInvariant() ?? string.Empty }).ToList(),
        CountryOverrides = CountryOverrides.ToDictionary(kv => kv.Key.Trim().ToUpperInvariant(), kv => string.IsNullOrWhiteSpace(kv.Value) ? null : kv.Value.Trim()),
        LastModifiedAt = LastModifiedAt,
    };

    /// <summary>Returns a validation message for a normalized configuration, or null when it can be saved.</summary>
    public string? Validate()
    {
        if (Regions.Count > MaxRegions)
        {
            return $"A tenant can have up to {MaxRegions} regions.";
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var region in Regions)
        {
            if (string.IsNullOrWhiteSpace(region.RegionId) || region.RegionId.Length > 64 || !ids.Add(region.RegionId))
            {
                return "Every region needs a unique id.";
            }

            if (region.Code.Length is 0 or > MaxCodeLength || !region.Code.All(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_'))
            {
                return $"Region codes must be 1 to {MaxCodeLength} letters, digits, hyphens or underscores.";
            }

            if (!codes.Add(region.Code))
            {
                return $"Region {region.Code} is listed more than once.";
            }
        }

        foreach (var (country, regionId) in CountryOverrides)
        {
            if (!CountryRegionDefaults.IsKnownCountry(country))
            {
                return $"{country} is not an ISO-3166 country code.";
            }

            if (regionId is not null && !ids.Contains(regionId))
            {
                return $"{country} is mapped to a region that does not exist.";
            }
        }

        return null;
    }
}

/// <summary>
/// Built-in country-to-region map, based on the UN M49 geographic regions: the Americas go to
/// AMER; Eastern, South-eastern and Southern Asia and Oceania to APAC; everything else to EMEA.
/// Uninhabited territories start with no region.
/// </summary>
public static class CountryRegionDefaults
{
    /// <summary>Id of the built-in EMEA region.</summary>
    public const string EmeaRegionId = "emea";

    /// <summary>Id of the built-in AMER region.</summary>
    public const string AmerRegionId = "amer";

    /// <summary>Id of the built-in APAC region.</summary>
    public const string ApacRegionId = "apac";

    /// <summary>Every ISO-3166-1 alpha-2 code the portal offers, matching the portal's country list.</summary>
    public static IReadOnlyList<string> CountryCodes { get; } = Split(
        "AD AE AF AG AI AL AM AO AQ AR AS AT AU AW AX AZ BA BB BD BE BF BG BH BI BJ BL BM BN BO BQ BR BS " +
        "BT BV BW BY BZ CA CC CD CF CG CH CI CK CL CM CN CO CR CU CV CW CX CY CZ DE DJ DK DM DO DZ EC EE " +
        "EG EH ER ES ET FI FJ FK FM FO FR GA GB GD GE GF GG GH GI GL GM GN GP GQ GR GS GT GU GW GY HK HM " +
        "HN HR HT HU ID IE IL IM IN IO IQ IR IS IT JE JM JO JP KE KG KH KI KM KN KP KR KW KY KZ LA LB LC " +
        "LI LK LR LS LT LU LV LY MA MC MD ME MF MG MH MK ML MM MN MO MP MQ MR MS MT MU MV MW MX MY MZ NA " +
        "NC NE NF NG NI NL NO NP NR NU NZ OM PA PE PF PG PH PK PL PM PN PR PS PT PW PY QA RE RO RS RU RW " +
        "SA SB SC SD SE SG SH SI SJ SK SL SM SN SO SR SS ST SV SX SY SZ TC TD TF TG TH TJ TK TL TM TN TO " +
        "TR TT TV TW TZ UA UG UM US UY UZ VA VC VE VG VI VN VU WF WS YE YT ZA ZM ZW");

    private static readonly HashSet<string> Known = new(CountryCodes, StringComparer.Ordinal);

    private static readonly HashSet<string> Amer = new(Split(
        "AG AI AR AW BB BL BM BO BQ BR BS BZ CA CL CO CR CU CW DM DO EC FK GD GF GL GP GT GY HN HT JM KN " +
        "KY LC MF MQ MS MX NI PA PE PM PR PY SR SV SX TC TT US UY VC VE VG VI"), StringComparer.Ordinal);

    private static readonly HashSet<string> Apac = new(Split(
        "CN HK JP KP KR MN MO TW BN ID KH LA MM MY PH SG TH TL VN AF BD BT IN IR LK MV NP PK " +
        "AS AU CC CK CX FJ FM GU KI MH MP NC NF NR NU NZ PF PG PN PW SB TK TO TV VU WF WS"), StringComparer.Ordinal);

    private static readonly HashSet<string> Unassigned = new(Split("AQ BV GS HM TF UM"), StringComparer.Ordinal);

    /// <summary>True when <paramref name="countryCode"/> is in <see cref="CountryCodes"/>.</summary>
    public static bool IsKnownCountry(string countryCode) => Known.Contains(countryCode);

    /// <summary>Built-in region id for a country, or null for uninhabited and unknown codes.</summary>
    public static string? RegionIdFor(string countryCode) =>
        !Known.Contains(countryCode) || Unassigned.Contains(countryCode) ? null
        : Amer.Contains(countryCode) ? AmerRegionId
        : Apac.Contains(countryCode) ? ApacRegionId
        : EmeaRegionId;

    private static string[] Split(string codes) => codes.Split(' ', StringSplitOptions.RemoveEmptyEntries);
}
