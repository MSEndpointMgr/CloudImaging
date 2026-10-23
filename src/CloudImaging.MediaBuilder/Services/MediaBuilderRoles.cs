namespace CloudImaging.MediaBuilder.Services;

/// <summary>
/// Media Builder app roles and their implication hierarchy: Administrator implies Operator,
/// which implies Technician. The same edges are listed in the Portal frontend and backend.
/// </summary>
public static class MediaBuilderRoles
{
    /// <summary>App role value for Cloud Imaging administrators.</summary>
    public const string Administrator = "CloudImaging.Administrator";

    /// <summary>App role value for operators, who may generate boot images.</summary>
    public const string Operator = "CloudImaging.Operator";

    /// <summary>App role value for technicians, who may prepare USB storage devices.</summary>
    public const string Technician = "CloudImaging.Technician";

    /// <summary>
    /// Roles implied by each held role. Each entry lists its full closure, so expanding needs a
    /// single lookup per held role.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Implications { get; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            [Administrator] = [Operator, Technician],
            [Operator] = [Technician],
            [Technician] = [],
        };

    /// <summary>
    /// Returns the held Media Builder roles plus every role they imply. Values are compared
    /// ordinally; unknown values (including differently cased role names) are ignored.
    /// </summary>
    public static IReadOnlySet<string> Expand(IEnumerable<string> roles)
    {
        ArgumentNullException.ThrowIfNull(roles);

        var expanded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var role in roles)
        {
            if (!Implications.TryGetValue(role, out var implied))
            {
                continue;
            }

            expanded.Add(role);
            expanded.UnionWith(implied);
        }
        return expanded;
    }
}
