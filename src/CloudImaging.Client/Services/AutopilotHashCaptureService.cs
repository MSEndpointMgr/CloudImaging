using System.Diagnostics;
using System.IO;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CloudImaging.Client.Services;

/// <summary>
/// Captures the Windows Autopilot 4K hardware hash inside WinPE. The MDM_DevDetail_Ext01 WMI
/// class that Get-WindowsAutopilotInfo relies on does not exist in WinPE, so the hash comes from
/// the ADK's OA3Tool in report mode. Media Builder stages OA3Tool next to the Client and services
/// the WinPE-PlatformId component, which supplies the Platform Crypto Provider (PCPKsp.dll) that
/// OA3Tool needs to include TPM data.
/// </summary>
public sealed partial class AutopilotHashCaptureService
{
    public const string ToolRelativeDirectory = @"Tools\Autopilot";
    public const string Oa3ToolFileName = "oa3tool.exe";
    public const string TpmProviderFileName = "PCPKsp.dll";

    private static readonly TimeSpan ToolTimeout = TimeSpan.FromMinutes(3);

    // OA3Tool requires an input key file even in /NoKeyCheck report mode; these placeholder values are never validated.
    private const string InputKeyXml = """
        <?xml version="1.0"?>
        <Key>
          <ProductKey>XXXXX-XXXXX-XXXXX-XXXXX-XXXXX</ProductKey>
          <ProductKeyID>0000000000000</ProductKeyID>
          <ProductKeyState>0</ProductKeyState>
        </Key>
        """;

    private const string ConfigXml = """
        <OA3>
          <FileBased>
            <InputKeyXMLFile>.\input.xml</InputKeyXMLFile>
          </FileBased>
          <OutputData>
            <AssembledBinaryFile>.\OA3.bin</AssembledBinaryFile>
            <ReportedXMLFile>.\OA3.xml</ReportedXMLFile>
          </OutputData>
        </OA3>
        """;

    private readonly ILogger<AutopilotHashCaptureService> _logger;
    private readonly string _toolPath;
    private readonly string _tpmProviderPath;
    private readonly Func<string, string, string, TimeSpan, CancellationToken, Task<(int ExitCode, string Output)>> _runProcess;
    private bool _tpmProviderRegistered;

    /// <param name="logger">Defaults to a no-op logger.</param>
    /// <param name="clientDirectory">Defaults to the Client's own install directory (X:\CloudImaging).</param>
    /// <param name="systemDirectory">Defaults to <see cref="Environment.SystemDirectory"/>.</param>
    /// <param name="runProcessOverride">Test seam: (fileName, arguments, workingDirectory, timeout, ct).</param>
    public AutopilotHashCaptureService(
        ILogger<AutopilotHashCaptureService>? logger = null,
        string? clientDirectory = null,
        string? systemDirectory = null,
        Func<string, string, string, TimeSpan, CancellationToken, Task<(int ExitCode, string Output)>>? runProcessOverride = null)
    {
        _logger = logger ?? NullLogger<AutopilotHashCaptureService>.Instance;
        _toolPath = Path.Combine(clientDirectory ?? AppContext.BaseDirectory, ToolRelativeDirectory, Oa3ToolFileName);
        _tpmProviderPath = Path.Combine(systemDirectory ?? Environment.SystemDirectory, TpmProviderFileName);
        _runProcess = runProcessOverride ?? RunProcessAsync;
    }

    /// <summary>True when this boot image was built with the Autopilot tooling.</summary>
    public bool IsToolingPresent => File.Exists(_toolPath) && File.Exists(_tpmProviderPath);

    /// <summary>Runs OA3Tool and returns the Base64 hardware hash. Throws with OA3Tool's output on failure.</summary>
    public async Task<string> CaptureAsync(CancellationToken ct = default)
    {
        if (!IsToolingPresent)
        {
            throw new InvalidOperationException("This boot image does not include the Autopilot hardware hash tooling. Rebuild it in Media Builder with Autopilot registration included.");
        }

        await EnsureTpmProviderRegisteredAsync(ct);

        var workDir = Path.Combine(Path.GetTempPath(), $"ci-oa3-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(workDir, "input.xml"), InputKeyXml, ct);
            await File.WriteAllTextAsync(Path.Combine(workDir, "OA3.cfg"), ConfigXml, ct);

            LogCaptureStarted(_logger);
            var (exitCode, output) = await _runProcess(_toolPath, "/Report /ConfigFile=.\\OA3.cfg /NoKeyCheck", workDir, ToolTimeout, ct);
            var reportPath = Path.Combine(workDir, "OA3.xml");
            if (exitCode != 0 || !File.Exists(reportPath))
            {
                LogCaptureFailed(_logger, exitCode, output);
                throw new InvalidOperationException($"OA3Tool could not read the hardware hash (exit code {exitCode}). {Summarize(output)}".Trim());
            }

            var hash = ParseHardwareHash(await File.ReadAllTextAsync(reportPath, ct));
            LogCaptureSucceeded(_logger, hash.Length);
            return hash;
        }
        finally
        {
            // The report holds the full hardware inventory; it is never left on the RAM disk.
            try { Directory.Delete(workDir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>Extracts the HardwareHash element from an OA3Tool report.</summary>
    internal static string ParseHardwareHash(string reportXml)
    {
        var document = XDocument.Parse(reportXml);
        var value = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "HardwareHash")?.Value.Trim();
        if (string.IsNullOrEmpty(value))
        {
            throw new InvalidOperationException("OA3Tool ran but its report contains no hardware hash.");
        }

        var buffer = new byte[value.Length];
        return Convert.TryFromBase64String(value, buffer, out _)
            ? value
            : throw new InvalidOperationException("OA3Tool returned a hardware hash that is not valid Base64.");
    }

    /// <summary>
    /// Registers PCPKsp.dll as a key storage provider through its DllInstall entry point so OA3Tool
    /// can reach the TPM; idempotent if the component already registered it. Done once per Client run.
    /// </summary>
    private async Task EnsureTpmProviderRegisteredAsync(CancellationToken ct)
    {
        if (_tpmProviderRegistered)
        {
            return;
        }

        var rundll32 = Path.Combine(Path.GetDirectoryName(_tpmProviderPath)!, "rundll32.exe");
        var (exitCode, output) = await _runProcess(rundll32, $"\"{_tpmProviderPath}\",DllInstall", Path.GetTempPath(), TimeSpan.FromSeconds(30), ct);
        if (exitCode != 0)
        {
            // Not fatal: the hash is still produced, only without TPM data.
            LogTpmRegistrationFailed(_logger, exitCode, output);
        }
        _tpmProviderRegistered = true;
    }

    private static string Summarize(string output)
    {
        var lastLines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(3);
        return string.Join(" ", lastLines);
    }

    private static async Task<(int ExitCode, string Output)> RunProcessAsync(string fileName, string arguments, string workingDirectory, TimeSpan timeout, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        process.Start();
        var stdOut = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var stdErr = process.StandardError.ReadToEndAsync(timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return (-1, $"{Path.GetFileName(fileName)} did not finish within {timeout.TotalSeconds:0} seconds.");
        }

        return (process.ExitCode, await stdOut + await stdErr);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Capturing the Autopilot hardware hash with OA3Tool.")]
    private static partial void LogCaptureStarted(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Autopilot hardware hash captured ({Length} characters, value omitted).")]
    private static partial void LogCaptureSucceeded(ILogger logger, int length);

    [LoggerMessage(Level = LogLevel.Error, Message = "OA3Tool failed with exit code {ExitCode}: {Output}")]
    private static partial void LogCaptureFailed(ILogger logger, int exitCode, string output);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Registering the TPM provider returned exit code {ExitCode}; the hash may lack TPM data. {Output}")]
    private static partial void LogTpmRegistrationFailed(ILogger logger, int exitCode, string output);
}
