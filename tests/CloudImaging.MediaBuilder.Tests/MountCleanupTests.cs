using System.Diagnostics;
using System.IO;
using CloudImaging.MediaBuilder.Services;
using FluentAssertions;
using Xunit;

namespace CloudImaging.MediaBuilder.Tests;

/// <summary>Leftover mounts from a killed run are undone from the working folder's journal; live runs are never touched.</summary>
public sealed class MountCleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"mountcleanup-tests-{Guid.NewGuid():N}");
    private readonly List<(string File, string Args)> _ran = [];

    public MountCleanupTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private Task<int> FakeRun(string file, string args)
    {
        _ran.Add((file, args));
        return Task.FromResult(0);
    }

    private string NewWorkDir(string prefix, string? ownerMarker)
    {
        var dir = Path.Combine(_root, $"{prefix}{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        if (ownerMarker is not null)
            File.WriteAllText(Path.Combine(dir, "owner.pid"), ownerMarker);
        return dir;
    }

    private static string LiveOwner()
    {
        using var current = Process.GetCurrentProcess();
        return $"{current.Id}|{current.ProcessName}|{current.StartTime.Ticks}";
    }

    // PID int.MaxValue never exists, so the owner reads as exited.
    private const string DeadOwner = "2147483647|CloudImaging.MediaBuilder|1";

    [Fact]
    public void Journal_RecordsAndForgetsMounts()
    {
        var dir = NewWorkDir("ci-bootimage-", LiveOwner());
        MountCleanup.RecordWimMount(dir, @"C:\Temp\Mount");
        MountCleanup.RecordDiskImage(dir, @"D:\ISO\Win11.iso");

        MountCleanup.ReadJournal(dir).Select(r => r.Path).Should().Equal(@"C:\Temp\Mount", @"D:\ISO\Win11.iso");

        MountCleanup.Forget(dir, @"C:\Temp\Mount");
        MountCleanup.ReadJournal(dir).Should().ContainSingle().Which.IsWim.Should().BeFalse();
    }

    [Fact]
    public async Task Sweep_UndoesJournaledMountsOfDeadRuns_AndDeletesTheirFolders()
    {
        var dead = NewWorkDir("ci-bootimage-", DeadOwner);
        MountCleanup.RecordWimMount(dead, Path.Combine(dead, "Mount"));
        MountCleanup.RecordDiskImage(dead, @"D:\ISO\Win11 O'Brien.iso");

        var count = await MountCleanup.SweepAsync(tempRoot: _root, runProcess: FakeRun);

        count.Should().Be(1);
        Directory.Exists(dead).Should().BeFalse();
        _ran.Should().Contain(r => r.Args == "/Cleanup-Mountpoints");
        _ran.Should().Contain(r => r.File == "dism.exe" && r.Args.Contains("/Unmount-Image") && r.Args.Contains(Path.Combine(dead, "Mount")) && r.Args.Contains("/Discard"));
        _ran.Should().Contain(r => r.File == "powershell.exe" && r.Args.Contains("'D:\\ISO\\Win11 O''Brien.iso'"));
    }

    [Fact]
    public async Task Sweep_NeverTouchesAFolderOwnedByARunningProcess()
    {
        var live = NewWorkDir("ci-deploy-", LiveOwner());
        MountCleanup.RecordWimMount(live, Path.Combine(live, "Mount"));

        var count = await MountCleanup.SweepAsync(tempRoot: _root, runProcess: FakeRun);

        count.Should().Be(0);
        Directory.Exists(live).Should().BeTrue();
        _ran.Should().BeEmpty();
    }

    [Fact]
    public async Task Sweep_HandlesFoldersFromBeforeTheJournal()
    {
        var generation = NewWorkDir("ci-bootimage-", ownerMarker: null);
        Directory.CreateDirectory(Path.Combine(generation, "Mount"));
        var deploy = NewWorkDir("ci-deploy-mount-", ownerMarker: null);

        await MountCleanup.SweepAsync(tempRoot: _root, runProcess: FakeRun);

        _ran.Should().Contain(r => r.Args.Contains(Path.Combine(generation, "Mount")));
        _ran.Should().Contain(r => r.Args.Contains($"/MountDir:\"{deploy}\""));
        Directory.Exists(generation).Should().BeFalse();
        Directory.Exists(deploy).Should().BeFalse();
    }

    [Fact]
    public async Task Sweep_IgnoresIpcFolders()
    {
        var ipc = NewWorkDir("ci-elevated-", DeadOwner);

        (await MountCleanup.SweepAsync(tempRoot: _root, runProcess: FakeRun)).Should().Be(0);
        Directory.Exists(ipc).Should().BeTrue();
    }

    [Fact]
    public async Task Worker_CancelsWhenItsParentHasExited()
    {
        var ipc = NewWorkDir("ci-elevated-", DeadOwner);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await ElevationHelper.WatchForCancelOrParentExitAsync(Path.Combine(ipc, "cancel.flag"), cts);

        cts.IsCancellationRequested.Should().BeTrue();
        ElevationHelper.GetOwnerState(ipc).Should().Be(ElevationHelper.OwnerState.Gone);
    }

    [Fact]
    public void Worker_KeepsRunningWhenTheOwnerIsUnknownOrAlive()
    {
        ElevationHelper.GetOwnerState(NewWorkDir("ci-elevated-", ownerMarker: null)).Should().Be(ElevationHelper.OwnerState.Unknown);
        ElevationHelper.GetOwnerState(NewWorkDir("ci-elevated-", LiveOwner())).Should().Be(ElevationHelper.OwnerState.Alive);
    }
}
