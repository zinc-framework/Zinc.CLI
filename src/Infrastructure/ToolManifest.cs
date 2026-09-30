namespace Zinc.Cli;

/// <summary>The local tool manifest that pins zinc's version for a project.</summary>
static class ToolManifest
{
    /// <summary>
    /// The manifest in <paramref name="root"/>, if any. Older SDKs put it in .config/, newer ones at
    /// the root; dotnet reads either.
    /// </summary>
    public static string? Find(string root) =>
        new[] { Path.Combine(root, "dotnet-tools.json"), Path.Combine(root, ".config", "dotnet-tools.json") }
            .FirstOrDefault(File.Exists);
}
