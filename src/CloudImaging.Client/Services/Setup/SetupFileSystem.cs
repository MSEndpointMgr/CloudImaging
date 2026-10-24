using System.IO;

namespace CloudImaging.Client.Services.Setup;

/// <summary>
/// File operations on the applied Windows volume, with paths relative to its root (for example
/// <c>Windows\Panther\unattend.xml</c>). One interface so the Prepare setup step can be tested
/// without a real volume.
/// </summary>
public interface ISetupFileSystem
{
    /// <summary>Creates the directory and its parents when missing.</summary>
    void CreateDirectory(string relativePath);

    /// <summary>Writes the file, replacing any existing one.</summary>
    void WriteAllBytes(string relativePath, byte[] bytes);

    /// <summary>Reads the file back.</summary>
    byte[] ReadAllBytes(string relativePath);

    /// <summary>Creates or replaces the file, creating its directory, and returns it open for writing.</summary>
    Stream Create(string relativePath);

    /// <summary>Opens the file for reading.</summary>
    Stream OpenRead(string relativePath);

    /// <summary>Copies the file, replacing the destination and creating its directory.</summary>
    void CopyFile(string sourceRelativePath, string destinationRelativePath);

    /// <summary>Deletes the file when it exists.</summary>
    void DeleteFile(string relativePath);

    /// <summary>Deletes the directory and everything under it when it exists.</summary>
    void DeleteTree(string relativePath);

    /// <summary>True when the file exists.</summary>
    bool FileExists(string relativePath);

    /// <summary>Absolute path, for handing to a process.</summary>
    string FullPath(string relativePath);
}

/// <summary><see cref="ISetupFileSystem"/> over a mounted volume such as <c>W:\</c>.</summary>
public sealed class SetupFileSystem(string volumeRoot) : ISetupFileSystem
{
    private readonly string _root = Path.GetFullPath(volumeRoot.EndsWith('\\') ? volumeRoot : volumeRoot + "\\");

    /// <summary>Absolute path for a relative one; refuses anything that would leave the volume.</summary>
    public string Resolve(string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException($"\"{relativePath}\" must be relative to the volume root.", nameof(relativePath));
        }
        var full = Path.GetFullPath(Path.Combine(_root, relativePath));
        if (!full.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"\"{relativePath}\" leaves the volume.", nameof(relativePath));
        }
        return full;
    }

    /// <inheritdoc/>
    public void CreateDirectory(string relativePath) => Directory.CreateDirectory(Resolve(relativePath));

    /// <inheritdoc/>
    public void WriteAllBytes(string relativePath, byte[] bytes)
    {
        var path = Resolve(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    /// <inheritdoc/>
    public byte[] ReadAllBytes(string relativePath) => File.ReadAllBytes(Resolve(relativePath));

    /// <inheritdoc/>
    public Stream Create(string relativePath)
    {
        var path = Resolve(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81_920, useAsync: true);
    }

    /// <inheritdoc/>
    public Stream OpenRead(string relativePath) => new FileStream(Resolve(relativePath), FileMode.Open, FileAccess.Read, FileShare.Read, 81_920);

    /// <inheritdoc/>
    public void CopyFile(string sourceRelativePath, string destinationRelativePath)
    {
        var destination = Resolve(destinationRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(Resolve(sourceRelativePath), destination, overwrite: true);
    }

    /// <inheritdoc/>
    public void DeleteFile(string relativePath)
    {
        var path = Resolve(relativePath);
        if (File.Exists(path))
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
    }

    /// <inheritdoc/>
    public void DeleteTree(string relativePath)
    {
        var path = Resolve(relativePath);
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    /// <inheritdoc/>
    public bool FileExists(string relativePath) => File.Exists(Resolve(relativePath));

    /// <inheritdoc/>
    public string FullPath(string relativePath) => Resolve(relativePath);
}
