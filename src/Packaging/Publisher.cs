namespace Zinc.Cli.Packaging;

/// <summary>Runs dotnet publish for a platform into a folder zinc owns.</summary>
static class Publisher
{
    /// <summary>Where zinc publishes <paramref name="rid"/> before packaging it.</summary>
    public static string StagingDirectory(string root, string rid) => Path.Combine(root, "dist", ".stage", rid);

    /// <param name="platformArgs">Properties the platform's package needs to be correct, e.g. single-file on macOS.</param>
    /// <param name="userArgs">Whatever the user passed after --. Comes last, so it can override anything.</param>
    public static async Task<string> PublishAsync(GameProject project, string rid, IReadOnlyList<string> platformArgs,
        IReadOnlyList<string> userArgs, CancellationToken ct)
    {
        var publishDir = Path.Combine(StagingDirectory(project.RootDirectory, rid), "publish");

        // Start clean: dotnet publish -o never deletes files, so anything a previous publish left
        // behind would otherwise end up in the package.
        Files.DeleteDirectory(publishDir);

        Ui.Step($"Publishing {project.Name} for {rid}");
        await Run.Tool("dotnet",
            [
                "publish", project.ProjectPath, "-c", "Release", "-r", rid, "--self-contained", "-o", publishDir,
                // Packages don't ship .pdb files: the game's own, then its references' (Zinc and the rest).
                // They're still built into bin/Release.
                "-p:CopyOutputSymbolsToPublishDirectory=false", "-p:PublishReferencesSymbols=false",
                .. platformArgs, .. userArgs,
            ],
            $"Publishing {project.Name}", ct);

        if (!PublishManifest.ExistsIn(publishDir)) throw MissingManifest(project);
        return publishDir;
    }

    static ZincException MissingManifest(GameProject project)
    {
        const string problem = $"dotnet publish didn't write {PublishManifest.FileName}.";
        var zinc = project.ZincProjectPath;
        if (zinc == null) return new ZincException(problem);

        var targets = Path.Combine(Path.GetDirectoryName(zinc)!, "Zinc.Publish.targets");
        if (!File.Exists(targets) || !File.ReadAllText(targets).Contains("ZincWritePublishManifest"))
            return new ZincException(problem, "This version of Zinc predates it. Update the Zinc submodule.");

        var import = Path.GetRelativePath(project.ProjectDirectory, targets).Replace('/', '\\');
        return new ZincException(problem,
            $"Add this to {Path.GetFileName(project.ProjectPath)}, after the Zinc ProjectReference:\n" +
            $"  <Import Project=\"{import}\" />");
    }
}
