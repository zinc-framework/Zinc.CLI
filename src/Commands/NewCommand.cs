using System.ComponentModel;
using System.Text.RegularExpressions;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Zinc.Cli.Commands;

sealed class NewSettings : CommandSettings
{
    [CommandArgument(0, "[NAME]")]
    [Description("The game's name. Also the folder it's created in.")]
    public string? Name { get; set; }

    [CommandOption("--template <REPO>")]
    [Description("Git repository to start from.")]
    [DefaultValue("https://github.com/zinc-framework/Zinc.Template")]
    public string Template { get; set; } = "";

    [CommandOption("--branch <BRANCH>")]
    [Description("Branch or tag of the template to use.")]
    public string? Branch { get; set; }

    [CommandOption("-v|--verbose")]
    [Description("Show the output of every command zinc runs.")]
    public bool Verbose { get; set; }
}

/// <summary>
/// Clones the template as a new repository: fresh history, the template's submodules (Zinc) re-added
/// at the commits the template pins, the solution and assembly named after the game, and this
/// version of zinc pinned so `dnx zinc` in the project always runs the same one.
/// </summary>
[Description("Create a new game from the Zinc template.")]
sealed partial class NewCommand : AsyncCommand<NewSettings>
{
    sealed record Submodule(string Name, string Path, string Url, string? Branch, string Commit);

    public override async Task<int> ExecuteAsync(CommandContext context, NewSettings settings, CancellationToken ct)
    {
        Run.Verbose = settings.Verbose;

        var name = settings.Name ?? (AnsiConsole.Profile.Capabilities.Interactive
            ? AnsiConsole.Ask<string>("Name of the game:")
            : throw new ZincException("Give the game a name: zinc new <NAME>"));
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or "..")
            throw new ZincException($"'{name}' can't be used as a folder name.");

        var dir = Path.GetFullPath(name);
        if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any())
            throw new ZincException($"{Ui.Path(dir)} already exists and isn't empty.");

        Ui.Step($"Creating {name} from {settings.Template}");
        await Run.Tool("git", ["clone", "--depth", "1", .. settings.Branch != null ? ["--branch", settings.Branch] : Array.Empty<string>(), settings.Template, dir],
            "Cloning the template", ct);

        // Read what the template pins before its history goes.
        var submodules = await ReadSubmodulesAsync(dir, ct);
        Files.DeleteDirectory(Path.Combine(dir, ".git"));
        File.Delete(Path.Combine(dir, ".gitmodules"));

        Ui.Step("Starting a fresh repository");
        await Run.Tool("git", ["init", "--quiet"], "Initializing", ct, dir);
        foreach (var sub in submodules)
        {
            Files.DeleteDirectory(Path.Combine(dir, sub.Path));
            await Run.Tool("git", ["submodule", "add", "--name", sub.Name, .. sub.Branch != null ? ["-b", sub.Branch] : Array.Empty<string>(), sub.Url, sub.Path],
                $"Adding {sub.Name}", ct, dir);
            // submodule add checks out the branch tip; the template's pin is the tested combination.
            await Run.Tool("git", ["-C", sub.Path, "checkout", "--quiet", sub.Commit], $"Checking out {sub.Name} at {sub.Commit[..7]}", ct, dir);
            await Run.Tool("git", ["add", sub.Path], "Staging", ct, dir);
        }
        if (submodules.Count > 0)
            await Run.Tool("git", ["submodule", "update", "--init", "--recursive"], "Fetching nested submodules", ct, dir);

        Ui.Step($"Naming it {name}");
        var identifier = Identifier(name);
        RenameSolutions(dir, identifier);
        var project = SetAssemblyName(dir, identifier);

        await PinToolAsync(dir, ct);

        Ui.Blank();
        Ui.Success($"Created {Ui.Path(dir)}");
        Ui.Info($"cd {Run.Quote(Ui.Path(dir))}");
        if (project != null) Ui.Info($"dotnet run --project {Path.GetRelativePath(dir, project.ProjectDirectory)}");
        return 0;
    }

    static async Task<List<Submodule>> ReadSubmodulesAsync(string repo, CancellationToken ct)
    {
        if (!File.Exists(Path.Combine(repo, ".gitmodules"))) return [];

        var config = await Run.Query("git", ["config", "--file", ".gitmodules", "--get-regexp", @"^submodule\."], ct, repo);
        var entries = SubmoduleConfigLine().Matches(config.StdOut)
            .GroupBy(m => m.Groups["name"].Value)
            .Select(g => (Name: g.Key, Values: g.ToDictionary(m => m.Groups["key"].Value, m => m.Groups["value"].Value.Trim())));

        var submodules = new List<Submodule>();
        foreach (var (subName, values) in entries)
        {
            if (!values.TryGetValue("path", out var path) || !values.TryGetValue("url", out var url)) continue;
            // "160000 commit <sha>\t<path>": the commit the template pins.
            var tree = await Run.Query("git", ["ls-tree", "HEAD", path], ct, repo);
            var commit = tree.StdOut.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(2)
                ?? throw new ZincException($"The template lists submodule {subName} but doesn't pin a commit for it.");
            submodules.Add(new Submodule(subName, path, url, values.GetValueOrDefault("branch"), commit));
        }
        return submodules;
    }

    /// <summary>The template's Zinc.Template.sln (or .slnx) becomes &lt;Name&gt;.sln.</summary>
    static void RenameSolutions(string dir, string identifier)
    {
        foreach (var solution in Directory.EnumerateFiles(dir, "*.sln").Concat(Directory.EnumerateFiles(dir, "*.slnx")).ToList())
        {
            var renamed = Path.Combine(dir, identifier + Path.GetExtension(solution));
            if (solution == renamed) continue;
            File.Move(solution, renamed);
            Ui.Info($"{Path.GetFileName(solution)} → {Path.GetFileName(renamed)}");
        }
    }

    /// <summary>Sets AssemblyName so the executable (and the packaged app) carries the game's name.</summary>
    static GameProject? SetAssemblyName(string dir, string identifier)
    {
        var candidates = GameProject.FindCandidates(dir);
        if (candidates.Count != 1) return null;
        var project = new GameProject(candidates[0]);

        var xml = File.ReadAllText(project.ProjectPath);
        var existing = AssemblyNameElement().Match(xml);
        if (existing.Success)
        {
            xml = xml.Remove(existing.Index, existing.Length).Insert(existing.Index, $"<AssemblyName>{identifier}</AssemblyName>");
        }
        else
        {
            // Match the indentation of the first property in the first PropertyGroup.
            var group = FirstPropertyGroup().Match(xml);
            if (!group.Success) return project;
            var indent = group.Groups["indent"].Value;
            xml = xml.Insert(group.Index + group.Length, $"<AssemblyName>{identifier}</AssemblyName>{indent}");
        }
        File.WriteAllText(project.ProjectPath, xml);
        Ui.Info($"AssemblyName = {identifier} in {Path.GetRelativePath(dir, project.ProjectPath)}");
        return project;
    }

    /// <summary>
    /// Pins this version of zinc in a local tool manifest, so `dnx zinc` inside the project runs the
    /// version it was created with. Skipped with a warning when this version isn't on NuGet (a local build).
    /// </summary>
    static async Task PinToolAsync(string dir, CancellationToken ct)
    {
        var version = ToolInfo.Version;
        var manifest = await Run.Query("dotnet", ["new", "tool-manifest"], ct, dir);
        var install = manifest.Success
            ? await Run.Query("dotnet", ["tool", "install", "zinc", "--local", "--version", version], ct, dir)
            : manifest;
        var created = ToolManifest.Find(dir);
        if (install.Success && created != null)
        {
            Ui.Info($"Pinned zinc {version} in {Path.GetRelativePath(dir, created)}");
            return;
        }

        if (created != null) File.Delete(created);
        Ui.Warn($"Couldn't pin zinc {version} in the project (is this version on NuGet?). `dnx zinc` there will run the latest.");
    }

    static string Identifier(string name)
    {
        var cleaned = NonIdentifierChars().Replace(name, "");
        return cleaned.Length > 0 ? cleaned : "Game";
    }

    [GeneratedRegex(@"^submodule\.(?<name>.+)\.(?<key>path|url|branch) (?<value>.*)$", RegexOptions.Multiline)]
    private static partial Regex SubmoduleConfigLine();

    [GeneratedRegex(@"<AssemblyName>[^<]*</AssemblyName>")]
    private static partial Regex AssemblyNameElement();

    [GeneratedRegex(@"<PropertyGroup>(?<indent>\r?\n[ \t]*)")]
    private static partial Regex FirstPropertyGroup();

    [GeneratedRegex(@"[^A-Za-z0-9_.-]")]
    private static partial Regex NonIdentifierChars();
}
