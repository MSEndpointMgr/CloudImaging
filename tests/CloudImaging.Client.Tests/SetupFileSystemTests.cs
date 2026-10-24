using System.IO;
using CloudImaging.Client.Services.Setup;
using FluentAssertions;
using Xunit;

namespace CloudImaging.Client.Tests;

/// <summary>File operations on the applied volume, over a temporary folder standing in for <c>W:\</c>.</summary>
public sealed class SetupFileSystemTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ci-setupfs-{Guid.NewGuid():N}");
    private readonly SetupFileSystem _volume;

    public SetupFileSystemTests()
    {
        Directory.CreateDirectory(_root);
        _volume = new SetupFileSystem(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Create_MakesTheFolder_AndOpenReadReturnsWhatWasWritten()
    {
        await using (var file = _volume.Create(@"Windows\Temp\CloudImaging\Texts\a.ps1"))
        {
            await file.WriteAsync(new byte[] { 1, 2, 3 });
        }

        using var read = _volume.OpenRead(@"Windows\Temp\CloudImaging\Texts\a.ps1");
        using var copy = new MemoryStream();
        await read.CopyToAsync(copy);
        copy.ToArray().Should().Equal(1, 2, 3);
    }

    [Fact]
    public void CopyFile_CreatesTheDestinationFolder_AndReplacesAnExistingFile()
    {
        _volume.WriteAllBytes(@"Texts\a.ps1", [7]);
        _volume.WriteAllBytes(@"Scripts\0001.ps1", [9, 9]);

        _volume.CopyFile(@"Texts\a.ps1", @"Scripts\0001.ps1");
        _volume.CopyFile(@"Texts\a.ps1", @"Setup\Scripts\0001.ps1");

        _volume.ReadAllBytes(@"Scripts\0001.ps1").Should().Equal(7);
        _volume.ReadAllBytes(@"Setup\Scripts\0001.ps1").Should().Equal(7);
    }

    [Fact]
    public void DeleteFile_AndDeleteTree_RemoveWhatExists_AndIgnoreWhatDoesNot()
    {
        _volume.WriteAllBytes(@"Windows\Temp\CloudImaging\Scripts\0001.ps1", [1]);
        File.SetAttributes(_volume.FullPath(@"Windows\Temp\CloudImaging\Scripts\0001.ps1"), FileAttributes.ReadOnly);
        _volume.CreateDirectory(@"Windows\Temp\CloudImaging\Texts");

        _volume.DeleteFile(@"Windows\Temp\CloudImaging\Scripts\0001.ps1");
        _volume.DeleteFile(@"Windows\Temp\CloudImaging\Scripts\0002.ps1");
        _volume.FileExists(@"Windows\Temp\CloudImaging\Scripts\0001.ps1").Should().BeFalse();

        _volume.DeleteTree(@"Windows\Temp\CloudImaging");
        _volume.DeleteTree(@"Windows\Temp\CloudImaging");
        Directory.Exists(_volume.FullPath(@"Windows\Temp\CloudImaging")).Should().BeFalse();
    }

    [Theory]
    [InlineData(@"..\outside.ps1")]
    [InlineData(@"C:\Windows\outside.ps1")]
    public void PathsOutsideTheVolume_AreRefused(string path)
    {
        var create = () => _volume.Create(path);
        var copy = () => _volume.CopyFile(path, "inside.ps1");

        create.Should().Throw<ArgumentException>();
        copy.Should().Throw<ArgumentException>();
    }
}
