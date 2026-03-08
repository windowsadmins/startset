namespace StartSet.Tests.Helpers;

/// <summary>
/// Creates a temporary directory that is automatically cleaned up on dispose.
/// </summary>
public sealed class TempDirectory : IDisposable
{
    public string Path { get; }

    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "StartSetTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    /// <summary>
    /// Creates a subdirectory inside the temp directory.
    /// </summary>
    public string CreateSubDirectory(string name)
    {
        var path = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Creates a file with the specified content inside the temp directory.
    /// </summary>
    public string CreateFile(string relativePath, string content)
    {
        var filePath = System.IO.Path.Combine(Path, relativePath);
        var dir = System.IO.Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(filePath, content);
        return filePath;
    }

    /// <summary>
    /// Creates a file with specified byte content.
    /// </summary>
    public string CreateFile(string relativePath, byte[] content)
    {
        var filePath = System.IO.Path.Combine(Path, relativePath);
        var dir = System.IO.Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllBytes(filePath, content);
        return filePath;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
        catch
        {
            // Best effort cleanup
        }
    }
}
