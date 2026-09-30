namespace Zinc.Cli;

static class Files
{
    /// <summary>Deletes a directory tree if it exists, including read-only files (git marks its objects read-only on Windows).</summary>
    public static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }

    public static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
    }

    /// <summary>Copies a file or a directory tree to <paramref name="destination"/>.</summary>
    public static void Copy(string source, string destination)
    {
        if (Directory.Exists(source)) CopyDirectory(source, destination);
        else File.Copy(source, destination);
    }

    public static string Size(string path)
    {
        var bytes = Directory.Exists(path)
            ? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length)
            : new FileInfo(path).Length;
        return bytes >= 1 << 20 ? $"{bytes / (double)(1 << 20):0.#} MB" : $"{bytes / 1024.0:0} KB";
    }
}
