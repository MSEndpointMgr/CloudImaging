using System.IO;
using System.Reflection.PortableExecutable;
using Microsoft.Win32;

namespace CloudImaging.MediaBuilder.Services;

/// <summary>A Windows service as registered in the image.</summary>
/// <param name="Name">Service key name.</param>
/// <param name="Start">Start type; 4 is disabled.</param>
/// <param name="ImagePath">ImagePath value, as written in the registry.</param>
/// <param name="ServiceDll">ServiceDll for a service hosted in svchost; null otherwise.</param>
/// <param name="DependOnService">Services and drivers it needs running first.</param>
/// <param name="ParametersReadable">False when the Parameters key denies reading (NSI does), so ServiceDll is unknown rather than absent.</param>
public sealed record ImageService(string Name, int? Start, string? ImagePath, string? ServiceDll, IReadOnlyList<string> DependOnService, bool ParametersReadable = true);

/// <summary>What the Wi-Fi check reads from a mounted Windows RE image. One interface so the rules are testable without an image.</summary>
public interface IRecoveryImageView
{
    /// <summary>The service, or null when the image has no such key.</summary>
    ImageService? GetService(string name);

    /// <summary>Services listed in a svchost group (SOFTWARE\Microsoft\Windows NT\CurrentVersion\Svchost).</summary>
    IReadOnlyList<string> SvchostGroup(string group);

    /// <summary>DLLs registered as netsh helpers (SOFTWARE\Microsoft\NetSh).</summary>
    IReadOnlyList<string> NetshHelpers();

    /// <summary>True when the file exists in the image; <paramref name="path"/> is relative to the Windows folder.</summary>
    bool FileExists(string path);

    /// <summary>DLLs <paramref name="path"/> statically imports; empty when it cannot be read.</summary>
    IReadOnlyList<string> ImportedDlls(string path);
}

/// <summary>
/// Checks, while the Windows RE image is still mounted, that everything the WLAN service needs is
/// in it: every service and driver the SCM starts before wlansvc, the files they run from, the
/// svchost groups that host them, the netsh wlan helper, and every DLL those load. Each of these
/// otherwise only shows up as "the service did not start" on real hardware.
/// </summary>
public static class WindowsRecoveryWifiCheck
{
    /// <summary>The service the Client starts; the SCM starts its dependencies first.</summary>
    public const string RootService = "WlanSvc";

    private const string NetshWlanHelper = "wlancfg.dll";
    private const int Disabled = 4;

    /// <summary>Everything missing for Wi-Fi in the image; empty when it is complete.</summary>
    public static IReadOnlyList<string> FindProblems(IRecoveryImageView image)
    {
        var problems = new List<string>();
        var binaries = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Name, string? NeededBy)>([(RootService, null)]);

        while (queue.Count > 0)
        {
            var (name, neededBy) = queue.Dequeue();
            if (!visited.Add(name))
                continue;

            var service = image.GetService(name);
            var label = service?.Name ?? name;
            var who = neededBy is null ? label : $"{label} (needed by {neededBy})";
            if (service is null)
            {
                problems.Add($"Service {who} is not registered in the image.");
                continue;
            }

            if (service.Start == Disabled)
                problems.Add($"Service {who} is disabled in the image.");

            var host = SvchostGroupOf(service.ImagePath);
            if (host is not null)
            {
                if (service.ServiceDll is { Length: > 0 } dll)
                    binaries.Add(dll);
                else if (service.ParametersReadable)
                    problems.Add($"Service {who} has no ServiceDll.");
                if (!image.SvchostGroup(host).Contains(service.Name, StringComparer.OrdinalIgnoreCase))
                    problems.Add($"Service {who} is not listed in the svchost group {host}.");
            }
            else
            {
                // A driver without ImagePath loads from System32\drivers\<name>.sys.
                binaries.Add(ToImageRelative(service.ImagePath) ?? Path.Combine("System32", "drivers", $"{service.Name}.sys"));
            }

            foreach (var dependency in service.DependOnService)
                queue.Enqueue((dependency, name));
        }

        if (!image.NetshHelpers().Contains(NetshWlanHelper, StringComparer.OrdinalIgnoreCase))
            problems.Add($"The netsh wlan helper ({NetshWlanHelper}) is not registered in the image.");
        binaries.Add(Path.Combine("System32", NetshWlanHelper));

        problems.AddRange(MissingFiles(image, binaries));
        return problems;
    }

    /// <summary>Each binary plus everything it statically imports, transitively; API sets resolve inside the loader.</summary>
    private static IEnumerable<string> MissingFiles(IRecoveryImageView image, IEnumerable<string> roots)
    {
        var checkedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Path, string? ImportedBy)>(roots.Select(r => (ToImageRelative(r) ?? r, (string?)null)));
        while (queue.Count > 0)
        {
            var (path, importedBy) = queue.Dequeue();
            if (!checkedFiles.Add(path))
                continue;

            if (!image.FileExists(path))
            {
                yield return importedBy is null
                    ? $"{Path.GetFileName(path)} is missing from the image."
                    : $"{Path.GetFileName(path)} (loaded by {importedBy}) is missing from the image.";
                continue;
            }

            foreach (var import in image.ImportedDlls(path))
            {
                if (IsApiSet(import))
                    continue;

                // Drivers import kernel modules from System32 and other drivers from System32\drivers.
                var candidate = Path.Combine("System32", import);
                var driver = Path.Combine("System32", "drivers", import);
                queue.Enqueue((image.FileExists(candidate) || !image.FileExists(driver) ? candidate : driver, Path.GetFileName(path)));
            }
        }
    }

    private static bool IsApiSet(string dll) =>
        dll.StartsWith("api-ms-", StringComparison.OrdinalIgnoreCase) || dll.StartsWith("ext-ms-", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads a service key, or null when it does not exist.</summary>
    internal static ImageService? ReadService(RegistryKey root, string keyPath)
    {
        using var key = root.OpenSubKey(keyPath);
        if (key is null)
            return null;

        var serviceDll = key.GetValue("ServiceDll", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        var readable = true;
        try
        {
            using var parameters = key.OpenSubKey("Parameters");
            serviceDll = parameters?.GetValue("ServiceDll", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? serviceDll;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            readable = false;
        }

        return new ImageService(
            Path.GetFileName(key.Name),
            key.GetValue("Start") as int?,
            key.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string,
            serviceDll,
            key.GetValue("DependOnService") as string[] ?? [],
            readable);
    }

    /// <summary>The group in "svchost.exe -k Group ...", or null when the service does not run in svchost.</summary>
    internal static string? SvchostGroupOf(string? imagePath)
    {
        if (imagePath is null || !imagePath.Contains("svchost.exe", StringComparison.OrdinalIgnoreCase))
            return null;
        var parts = imagePath.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var index = Array.FindIndex(parts, p => p.Equals("-k", StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < parts.Length ? parts[index + 1] : null;
    }

    /// <summary>A registry path such as <c>%SystemRoot%\System32\x.dll</c> or <c>system32\drivers\x.sys</c>, relative to the Windows folder.</summary>
    internal static string? ToImageRelative(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        var value = path.Trim().Trim('"');
        var space = value.IndexOf(".exe ", StringComparison.OrdinalIgnoreCase);
        if (space >= 0)
            value = value[..(space + 4)];

        foreach (var prefix in new[] { @"%SystemRoot%\", @"%windir%\", @"\SystemRoot\", @"X:\Windows\", @"C:\Windows\" })
        {
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return value[prefix.Length..];
        }
        return value.Contains('\\', StringComparison.Ordinal) ? value : Path.Combine("System32", value);
    }

    /// <summary>The DLL names in a PE file's import table.</summary>
    internal static IReadOnlyList<string> ReadImports(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            using var pe = new PEReader(stream);
            var directory = pe.PEHeaders.PEHeader?.ImportTableDirectory;
            if (directory is not { RelativeVirtualAddress: > 0 } table)
                return [];

            var names = new List<string>();
            var reader = pe.GetSectionData(table.RelativeVirtualAddress).GetReader();
            while (reader.RemainingBytes >= 20)
            {
                var originalFirstThunk = reader.ReadInt32();
                reader.ReadInt32();
                reader.ReadInt32();
                var nameRva = reader.ReadInt32();
                var firstThunk = reader.ReadInt32();
                if (originalFirstThunk == 0 && nameRva == 0 && firstThunk == 0)
                    break;

                var nameReader = pe.GetSectionData(nameRva).GetReader();
                var bytes = new List<byte>();
                while (nameReader.RemainingBytes > 0 && nameReader.ReadByte() is var b && b != 0)
                    bytes.Add(b);
                names.Add(System.Text.Encoding.ASCII.GetString([.. bytes]));
            }
            return names;
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

/// <summary>
/// The mounted image's SYSTEM and SOFTWARE hives, loaded under HKLM with reg.exe for the check and
/// unloaded on dispose. Every key handle is closed before unload, since an open one makes it fail
/// and would then block the DISM unmount.
/// </summary>
public sealed class MountedRecoveryImage : IRecoveryImageView, IAsyncDisposable
{
    /// <summary>HKLM names the hives load under; <see cref="MountCleanup"/> unloads them after a killed run.</summary>
    public static readonly string[] HiveNames = ["CI_WINRE_SYSTEM", "CI_WINRE_SOFTWARE"];

    private readonly string _windowsDir;
    private readonly Func<string, string, CancellationToken, Task> _run;
    private readonly string _controlSet;

    private MountedRecoveryImage(string windowsDir, Func<string, string, CancellationToken, Task> run, string controlSet)
    {
        _windowsDir = windowsDir;
        _run = run;
        _controlSet = controlSet;
    }

    /// <summary>Loads the hives of the image mounted at <paramref name="mountDir"/>.</summary>
    /// <param name="mountDir">DISM mount directory.</param>
    /// <param name="run">Runs (exe, arguments) and throws when it fails.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<MountedRecoveryImage> LoadAsync(string mountDir, Func<string, string, CancellationToken, Task> run, CancellationToken ct)
    {
        var windowsDir = Path.Combine(mountDir, "Windows");
        var config = Path.Combine(windowsDir, "System32", "config");
        await run("reg.exe", $"load HKLM\\{HiveNames[0]} \"{Path.Combine(config, "SYSTEM")}\"", ct);
        try
        {
            await run("reg.exe", $"load HKLM\\{HiveNames[1]} \"{Path.Combine(config, "SOFTWARE")}\"", ct);
        }
        catch
        {
            await UnloadAsync(run, HiveNames[0]);
            throw;
        }

        // An offline hive has no CurrentControlSet; Select\Current names the one that boots.
        int current;
        using (var select = Registry.LocalMachine.OpenSubKey($@"{HiveNames[0]}\Select"))
            current = select?.GetValue("Current") is int value ? value : 1;
        return new MountedRecoveryImage(windowsDir, run, $"ControlSet{current:000}");
    }

    /// <inheritdoc/>
    public ImageService? GetService(string name) =>
        WindowsRecoveryWifiCheck.ReadService(Registry.LocalMachine, $@"{HiveNames[0]}\{_controlSet}\Services\{name}");

    /// <inheritdoc/>
    public IReadOnlyList<string> SvchostGroup(string group)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"{HiveNames[1]}\Microsoft\Windows NT\CurrentVersion\Svchost");
        return key?.GetValue(group) as string[] ?? [];
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> NetshHelpers()
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"{HiveNames[1]}\Microsoft\NetSh");
        return key is null ? [] : [.. key.GetValueNames().Select(n => key.GetValue(n) as string).OfType<string>().Select(Path.GetFileName).OfType<string>()];
    }

    /// <inheritdoc/>
    public bool FileExists(string path) => File.Exists(Path.Combine(_windowsDir, path));

    /// <inheritdoc/>
    public IReadOnlyList<string> ImportedDlls(string path) => WindowsRecoveryWifiCheck.ReadImports(Path.Combine(_windowsDir, path));

    /// <summary>Unloads both hives.</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var hive in HiveNames)
            await UnloadAsync(_run, hive);
    }

    private static async Task UnloadAsync(Func<string, string, CancellationToken, Task> run, string hive)
    {
        // Registry handles opened by the managed wrapper can linger until finalized.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        try { await run("reg.exe", $"unload HKLM\\{hive}", CancellationToken.None); }
        catch { /* the sweep retries before the next unmount */ }
    }
}
