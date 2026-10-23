using System.IO;
using System.Net.Http;
using CloudImaging.Client.Services.Setup;
using CloudImaging.Contracts.Enums;
using Microsoft.Win32;

namespace CloudImaging.Client.Tests.TestDoubles;

/// <summary>HTTP handler that answers from a function and records every request with its body.</summary>
internal sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request, body));
        return respond(request);
    }
}

/// <summary>In-memory offline hives: records loads, writes and unloads in order.</summary>
internal sealed class FakeOfflineRegistryHive : IOfflineRegistryHive
{
    public List<string> Events { get; } = [];

    public Dictionary<(SetupRegistryHive Hive, string Key, string Name), object> Values { get; } = [];

    public Dictionary<SetupRegistryHive, string> LoadFailures { get; } = [];

    public Func<SetupRegistryHive, string, string, Exception?> SetFailure { get; set; } = (_, _, _) => null;

    public HashSet<SetupRegistryHive> Loaded { get; } = [];

    public void Load(SetupRegistryHive hive, string hiveFilePath)
    {
        Events.Add($"load {hive}");
        if (LoadFailures.TryGetValue(hive, out var message))
        {
            throw new OfflineHiveException(message);
        }
        Loaded.Add(hive);
    }

    public bool Unload(SetupRegistryHive hive)
    {
        Events.Add($"unload {hive}");
        Loaded.Remove(hive);
        return true;
    }

    public void SetValue(SetupRegistryHive hive, string key, string valueName, object value, RegistryValueKind kind)
    {
        if (!Loaded.Contains(hive))
        {
            throw new InvalidOperationException($"{hive} is not loaded.");
        }
        if (SetFailure(hive, key, valueName) is { } failure)
        {
            throw failure;
        }
        Events.Add($"set {hive} {key}\\{valueName}");
        Values[(hive, key, valueName)] = value;
    }

    public object? GetValue(SetupRegistryHive hive, string key, string valueName) =>
        Values.TryGetValue((hive, key, valueName), out var value) ? value : null;

    public void DeleteValue(SetupRegistryHive hive, string key, string valueName)
    {
        Events.Add($"delete {hive} {key}\\{valueName}");
        Values.Remove((hive, key, valueName));
    }
}

/// <summary>In-memory applied volume.</summary>
internal sealed class FakeSetupFileSystem(string root = @"W:\") : ISetupFileSystem
{
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Events { get; } = [];

    public bool FailWrites { get; set; }

    /// <summary>Writes whose relative path starts with this fail with a disk error.</summary>
    public string? FailWritesUnder { get; set; }

    public void CreateDirectory(string relativePath) => Events.Add($"mkdir {relativePath}");

    public void WriteAllBytes(string relativePath, byte[] bytes)
    {
        if (FailWrites || (FailWritesUnder is not null && relativePath.StartsWith(FailWritesUnder, StringComparison.OrdinalIgnoreCase)))
        {
            throw new IOException("The disk is full.");
        }
        Events.Add($"write {relativePath}");
        Files[relativePath] = bytes;
    }

    public byte[] ReadAllBytes(string relativePath) => Files[relativePath];

    public void DeleteFile(string relativePath)
    {
        Events.Add($"delete {relativePath}");
        Files.Remove(relativePath);
    }

    public void DeleteTree(string relativePath)
    {
        Events.Add($"rmdir {relativePath}");
        foreach (var key in Files.Keys.Where(k => k.StartsWith(relativePath + "\\", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            Files.Remove(key);
        }
    }

    public bool FileExists(string relativePath) => Files.ContainsKey(relativePath);

    public string FullPath(string relativePath) => Path.Combine(root, relativePath);
}

/// <summary>Records each request and answers from a function; never starts a process.</summary>
internal sealed class FakeRestrictedProcessRunner : IRestrictedProcessRunner
{
    public List<(RestrictedProcessRequest Request, string? ScriptText)> Runs { get; } = [];

    /// <summary>Reads the script file at run time, so tests can check what was written before it ran.</summary>
    public Func<string, string?> ReadScript { get; set; } = _ => null;

    public Func<RestrictedProcessRequest, RestrictedProcessResult> Respond { get; set; } =
        _ => new RestrictedProcessResult(RestrictedProcessOutcome.Exited, 0, TimeSpan.FromSeconds(1), ["done"]);

    public RestrictedProcessResult Run(RestrictedProcessRequest request, CancellationToken cancellationToken)
    {
        var path = request.Arguments.Split('"', StringSplitOptions.RemoveEmptyEntries).LastOrDefault(a => a.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase));
        Runs.Add((request, path is null ? null : ReadScript(path)));
        return Respond(request);
    }
}
