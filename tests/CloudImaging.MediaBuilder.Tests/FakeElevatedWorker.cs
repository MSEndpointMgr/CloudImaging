using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using CloudImaging.MediaBuilder.Services;

namespace CloudImaging.MediaBuilder.Tests;

/// <summary>
/// Stands in for the elevated boot image worker. Like the real one it starts before its
/// parameters exist and waits for them, then writes a canned result; a cancel flag that
/// arrives first makes it exit with a cancelled result instead.
/// </summary>
internal sealed class FakeElevatedWorker(string result = FakeElevatedWorker.SuccessResult, Action<string>? onParams = null)
{
    public const string SuccessResult = """{"Success":true,"WimPath":"C:\\out\\cloud-imaging-boot.wim","Sha256Hash":"deadbeef"}""";
    private const string CancelledResult = """{"Success":false,"WimPath":null,"Sha256Hash":null,"Error":"Boot image generation was cancelled."}""";

    public bool Started { get; private set; }

    public bool ReceivedParams { get; private set; }

    public bool WasStopped { get; private set; }

    public static Func<CancellationToken, Task<BootImageGenerationService.GenerationInputs>> Inputs(string clientBinariesPath = @"C:\DoesNotExist") =>
        _ => Task.FromResult(new BootImageGenerationService.GenerationInputs(clientBinariesPath));

    /// <summary>Use as the service's <c>startElevatedProcessOverride</c>.</summary>
    public Process Start(string exePath, string args)
    {
        Started = true;
        var files = Regex.Matches(args, "\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToArray();
        var (paramsFile, resultFile, cancelFile) = (files[0], files[2], files[3]);
        _ = Task.Run(async () =>
        {
            while (!File.Exists(paramsFile))
            {
                if (File.Exists(cancelFile))
                {
                    WasStopped = true;
                    Write(resultFile, CancelledResult);
                    return;
                }

                if (!Directory.Exists(Path.GetDirectoryName(paramsFile)))
                {
                    return;
                }

                await Task.Delay(20);
            }

            ReceivedParams = true;
            onParams?.Invoke(File.ReadAllText(paramsFile));
            Write(resultFile, result);
        });

        return Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { UseShellExecute = false, CreateNoWindow = true })!;
    }

    private static void Write(string resultFile, string json)
    {
        File.WriteAllText(resultFile + ".pending", json);
        File.Move(resultFile + ".pending", resultFile);
    }
}
