using System.Text.Json;
using System.Text.RegularExpressions;
using Spectre.Console;

namespace Zinc.Cli;

/// <summary>A game's .csproj: one that references Zinc.csproj.</summary>
sealed partial record GameProject(string ProjectPath)
{
    public string Name => Path.GetFileNameWithoutExtension(ProjectPath);
    public string ProjectDirectory => Path.GetDirectoryName(ProjectPath)!;

    /// <summary>The repository the project lives in; packages go in its dist/ folder.</summary>
    public string RootDirectory => FindRoot(ProjectDirectory);

    /// <summary>The Zinc.csproj this project references, resolved to a full path.</summary>
    public string? ZincProjectPath
    {
        get
        {
            var match = ZincReference().Match(File.ReadAllText(ProjectPath));
            if (!match.Success) return null;
            var relative = match.Groups[1].Value.Replace('\\', Path.DirectorySeparatorChar);
            return Path.GetFullPath(relative, ProjectDirectory);
        }
    }

    /// <summary>
    /// Uses <paramref name="explicitPath"/> when given (a .csproj or a folder holding one). Otherwise
    /// searches the repository around <paramref name="from"/> for game projects, asking when there
    /// are several and the console is interactive.
    /// </summary>
    public static GameProject Locate(string? explicitPath, string from)
    {
        if (explicitPath != null)
        {
            var full = Path.GetFullPath(explicitPath);
            if (File.Exists(full)) return new GameProject(full);
            if (Directory.Exists(full))
            {
                var inFolder = Directory.GetFiles(full, "*.csproj");
                if (inFolder.Length == 1) return new GameProject(inFolder[0]);
            }
            throw new ZincException($"No single .csproj at {explicitPath}.");
        }

        // A game project right here wins; otherwise search the whole repository.
        var here = Directory.GetFiles(from, "*.csproj").Where(ReferencesZinc).ToList();
        var candidates = here.Count == 1 ? here : FindCandidates(FindRoot(from));

        switch (candidates.Count)
        {
            case 0:
                throw new ZincException("Couldn't find a game project (a .csproj referencing Zinc.csproj) here.",
                    "Run zinc from inside the game's repository, or pass --project <path>.");
            case 1:
                return new GameProject(candidates[0]);
        }

        if (!AnsiConsole.Profile.Capabilities.Interactive)
            throw new ZincException("Found several game projects; pass --project to pick one:",
                string.Join('\n', candidates.Select(Ui.Path)));

        var picked = AnsiConsole.Prompt(new SelectionPrompt<string>()
            .Title("Which project?")
            .AddChoices(candidates.Select(Ui.Path)));
        return new GameProject(Path.GetFullPath(picked));
    }

    /// <summary>Walks up to the nearest folder with a .git (a directory, or a file for worktrees and submodules).</summary>
    public static string FindRoot(string start)
    {
        for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
        {
            var git = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(git) || File.Exists(git)) return dir.FullName;
        }
        return start;
    }

    /// <summary>Game projects under <paramref name="root"/>, skipping build output and dependency folders.</summary>
    public static List<string> FindCandidates(string root)
    {
        var found = new List<string>();
        Search(root, depth: 0);
        return found;

        void Search(string dir, int depth)
        {
            found.AddRange(Directory.GetFiles(dir, "*.csproj").Where(ReferencesZinc));
            if (depth == 4) return;
            foreach (var sub in Directory.GetDirectories(dir))
            {
                var name = Path.GetFileName(sub);
                if (name.StartsWith('.') || name is "bin" or "obj" or "dist" or "artifacts" or "node_modules") continue;
                Search(sub, depth + 1);
            }
        }
    }

    /// <summary>Evaluated MSBuild properties, e.g. AssemblyName or ZincBundleId. Missing ones come back empty.</summary>
    public async Task<Dictionary<string, string>> GetPropertiesAsync(IReadOnlyList<string> names, CancellationToken ct)
    {
        // With one -getProperty, dotnet prints the bare value; with several, JSON. Always ask for two.
        var asked = names.Count == 1 ? [names[0], "MSBuildProjectName"] : names;
        var result = await Run.Query("dotnet", ["msbuild", ProjectPath, .. asked.Select(n => $"-getProperty:{n}")], ct);
        if (!result.Success)
            throw new ZincException($"Couldn't evaluate {Path.GetFileName(ProjectPath)}.", result.Combined.Trim());

        var properties = JsonDocument.Parse(result.StdOut).RootElement.GetProperty("Properties");
        return names.ToDictionary(n => n, n => properties.TryGetProperty(n, out var v) ? v.GetString() ?? "" : "");
    }

    static bool ReferencesZinc(string csproj) => ZincReference().IsMatch(File.ReadAllText(csproj));

    [GeneratedRegex(@"<ProjectReference\s+Include=""([^""]*Zinc\.csproj)""", RegexOptions.IgnoreCase)]
    private static partial Regex ZincReference();
}
