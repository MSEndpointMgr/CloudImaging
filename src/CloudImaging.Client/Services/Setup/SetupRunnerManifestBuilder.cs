using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CloudImaging.Client.Services.Setup;

/// <summary>Kind of work the setup runner starts.</summary>
public enum SetupRunnerItemKind
{
    /// <summary>A driver installer handed over by Install drivers.</summary>
    DriverInstaller,

    /// <summary>A During Windows setup script from the profile.</summary>
    Script,
}

/// <summary>The setup runner's manifest, written to <c>Windows\Setup\CloudImaging\manifest.json</c>.</summary>
public sealed record SetupRunnerManifest(
    int SchemaVersion,
    string CreatedAt,
    Guid SessionId,
    SetupRunnerManifestProfile? Profile,
    IReadOnlyList<SetupRunnerManifestItem> Items,
    IReadOnlyList<string> CleanupPaths);

/// <summary>Which profile version the work comes from; null for driver work only.</summary>
public sealed record SetupRunnerManifestProfile(string Name, int Version);

/// <summary>One file the runner verifies before starting an item, relative to the system drive.</summary>
public sealed record SetupRunnerManifestFile(string Path, string Sha256);

/// <summary>How the runner starts an item; <c>{0}</c> in the arguments is the item's first file.</summary>
public sealed record SetupRunnerManifestCommand(string Executable, string Arguments, string WorkingDirectory);

/// <summary>One item, run in <see cref="Order"/>.</summary>
public sealed record SetupRunnerManifestItem(
    int Order,
    SetupRunnerItemKind Kind,
    string Name,
    IReadOnlyList<SetupRunnerManifestFile> Files,
    SetupRunnerManifestCommand Command,
    int TimeoutSeconds,
    IReadOnlyList<int> SuccessExitCodes,
    IReadOnlyList<string> CleanupPaths);

/// <summary>A During Windows setup script already written to the staging folder.</summary>
public sealed record StagedScript(string Name, string RelativePath, string Sha256, int TimeoutMinutes);

/// <summary>
/// Builds the setup runner's manifest: driver installers first in resolved order, then During
/// Windows setup scripts in profile order. Pure, so the order, paths and fields are unit-tested.
/// </summary>
public static class SetupRunnerManifestBuilder
{
    /// <summary>Format version the runner understands.</summary>
    public const int SchemaVersion = 1;

    /// <summary>Staging folder on the applied volume, relative to its root.</summary>
    public const string StagingFolder = @"Windows\Setup\CloudImaging";

    /// <summary>Folder of the staged scripts.</summary>
    public const string ScriptFolder = StagingFolder + @"\Scripts";

    /// <summary>The runner script in the staging folder.</summary>
    public const string RunnerPath = StagingFolder + @"\" + UnattendComposer.RunnerScriptName;

    /// <summary>The manifest in the staging folder.</summary>
    public const string ManifestPath = StagingFolder + @"\manifest.json";

    /// <summary>Windows PowerShell on the deployed OS, relative to the system drive.</summary>
    public const string PowerShellExecutable = @"Windows\System32\WindowsPowerShell\v1.0\powershell.exe";

    /// <summary>Arguments for a script item.</summary>
    public const string PowerShellArguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{0}\"";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Path of staged script number <paramref name="position"/> (1-based).</summary>
    public static string ScriptPath(int position) => string.Create(CultureInfo.InvariantCulture, $@"{ScriptFolder}\{position:00}.ps1");

    /// <summary>Builds the manifest. Throws <see cref="ArgumentException"/> for a rooted path or one containing <c>..</c>.</summary>
    public static SetupRunnerManifest Build(
        Guid sessionId,
        DateTimeOffset createdAt,
        string? profileName,
        int? profileVersion,
        IReadOnlyList<SpecializeInstaller> installers,
        IReadOnlyList<StagedScript> scripts)
    {
        var items = new List<SetupRunnerManifestItem>(installers.Count + scripts.Count);
        foreach (var installer in installers)
        {
            items.Add(new SetupRunnerManifestItem(
                items.Count + 1,
                SetupRunnerItemKind.DriverInstaller,
                installer.Name,
                [.. installer.Files.Select(f => new SetupRunnerManifestFile(Relative(f.Path), f.Sha256))],
                new SetupRunnerManifestCommand(Relative(installer.Command.Executable), installer.Command.Arguments, Relative(installer.Command.WorkingDirectory)),
                installer.TimeoutSeconds,
                [.. installer.SuccessExitCodes],
                [.. installer.CleanupPaths.Select(Relative)]));
        }
        foreach (var script in scripts)
        {
            items.Add(new SetupRunnerManifestItem(
                items.Count + 1,
                SetupRunnerItemKind.Script,
                script.Name,
                [new SetupRunnerManifestFile(Relative(script.RelativePath), script.Sha256)],
                new SetupRunnerManifestCommand(PowerShellExecutable, PowerShellArguments, ScriptFolder),
                script.TimeoutMinutes * 60,
                [0],
                []));
        }

        var profile = profileName is not null && profileVersion is { } version ? new SetupRunnerManifestProfile(profileName, version) : null;
        var created = createdAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        return new SetupRunnerManifest(SchemaVersion, created, sessionId, profile, items, [StagingFolder]);
    }

    /// <summary>UTF-8 without BOM, camelCase, enums as strings.</summary>
    public static byte[] Serialize(SetupRunnerManifest manifest) =>
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(JsonSerializer.Serialize(manifest, Json));

    /// <summary>Lowercase hex SHA-256 of the bytes as written.</summary>
    public static string Sha256(byte[] bytes) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));

    private static string Relative(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || System.IO.Path.IsPathRooted(path) || path.Split('\\', '/').Any(part => part == ".."))
        {
            throw new ArgumentException($"\"{path}\" must be relative to the system drive and must not contain \"..\".", nameof(path));
        }
        return path;
    }
}
