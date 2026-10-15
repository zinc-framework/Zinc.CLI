using System.IO.Compression;

namespace Zinc.Cli.Packaging;

/// <summary>
/// Turns a Windows publish into the folder players get: the publish as it is, since Zinc already
/// finds its content next to the executable on Windows, without zinc's own manifest.
/// </summary>
static class WindowsPackager
{
    public static string Assemble(PublishManifest manifest, string publishDir, string outputDir)
    {
        var folder = Path.Combine(outputDir, manifest.AppName);
        Ui.Step($"Assembling {Ui.Path(folder)}");

        if (!File.Exists(Path.Combine(publishDir, manifest.Executable)))
            throw new ZincException($"The publish has no executable '{manifest.Executable}'.");

        Files.DeleteDirectory(folder);
        Directory.CreateDirectory(folder);
        foreach (var entry in Directory.EnumerateFileSystemEntries(publishDir))
        {
            var name = Path.GetFileName(entry);
            if (name is PublishManifest.FileName) continue;
            Files.Copy(entry, Path.Combine(folder, name));
        }
        return folder;
    }

    /// <summary>The folder zipped with the folder itself at the top, so it unzips into one place.</summary>
    public static string Zip(string folder, PublishManifest manifest)
    {
        var zip = Path.Combine(Path.GetDirectoryName(folder)!, $"{manifest.AppName}-windows.zip");
        Ui.Step($"Zipping {Ui.Path(zip)}");
        File.Delete(zip);
        ZipFile.CreateFromDirectory(folder, zip, CompressionLevel.Optimal, includeBaseDirectory: true);
        return zip;
    }
}
