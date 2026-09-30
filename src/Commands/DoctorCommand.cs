using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Zinc.Cli.Packaging;

namespace Zinc.Cli.Commands;

sealed class DoctorSettings : CommandSettings
{
    [CommandOption("--project <PATH>")]
    [Description("The game's .csproj, or its folder. Found automatically when the repository has one.")]
    public string? Project { get; set; }

    [CommandOption("--notary-profile <PROFILE>")]
    [Description("Also check this notarytool keychain profile works. Contacts Apple.")]
    public string? NotaryProfile { get; set; }
}

[Description("Check this machine and project are set up to build and package.")]
sealed class DoctorCommand : AsyncCommand<DoctorSettings>
{
    enum Status { Ok, Warn, Fail, Info }

    sealed class Report
    {
        public readonly List<(Status Status, string Check, string Detail)> Rows = [];
        public readonly List<string> Fixes = [];

        public void Add(Status status, string check, string detail, string? fix = null)
        {
            Rows.Add((status, check, detail));
            if (fix != null) Fixes.Add(fix);
        }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, DoctorSettings settings, CancellationToken ct)
    {
        var report = new Report();
        await AnsiConsole.Status().StartAsync("Checking", async _ =>
        {
            await CheckToolsAsync(report, ct);
            if (OperatingSystem.IsMacOS()) await CheckMacSigningAsync(report, settings.NotaryProfile, ct);
            await CheckProjectAsync(report, settings.Project, ct);
        });

        var table = new Table().Border(TableBorder.Rounded).AddColumn("").AddColumn("Check").AddColumn("Details");
        foreach (var (status, check, detail) in report.Rows)
        {
            var glyph = status switch
            {
                Status.Ok => "[green]✓[/]",
                Status.Warn => "[yellow]![/]",
                Status.Fail => "[red]✗[/]",
                _ => "[grey]·[/]",
            };
            table.AddRow(glyph, Markup.Escape(check), Markup.Escape(detail));
        }
        AnsiConsole.Write(table);

        if (report.Fixes.Count > 0)
        {
            Ui.Blank();
            AnsiConsole.MarkupLine("[bold]To fix[/]");
            for (var i = 0; i < report.Fixes.Count; i++)
            {
                var lines = report.Fixes[i].Split('\n');
                Ui.Info($"{i + 1}. {lines[0]}");
                foreach (var line in lines.Skip(1)) Ui.Info($"   {line}");
            }
        }
        return report.Rows.Any(r => r.Status == Status.Fail) ? 1 : 0;
    }

    static async Task CheckToolsAsync(Report report, CancellationToken ct)
    {
        var sdk = await Run.Query("dotnet", ["--version"], ct);
        if (!sdk.Success)
        {
            report.Add(Status.Fail, ".NET SDK", "not found", "Install the .NET SDK: https://dot.net/download");
        }
        else
        {
            var version = sdk.StdOut.Trim();
            var major = int.TryParse(version.Split('.')[0], out var m) ? m : 0;
            if (major >= 10) report.Add(Status.Ok, ".NET SDK", version);
            else report.Add(Status.Warn, ".NET SDK", $"{version} (dnx needs 10 or later)", "Install the .NET 10 SDK or later: https://dot.net/download");
        }

        var git = await Run.Query("git", ["--version"], ct);
        if (git.Success) report.Add(Status.Ok, "git", git.StdOut.Trim().Replace("git version ", ""));
        else report.Add(Status.Fail, "git", "not found", "Install git: https://git-scm.com");
    }

    static async Task CheckMacSigningAsync(Report report, string? notaryProfile, CancellationToken ct)
    {
        var notarytool = await Run.Query("xcrun", ["--find", "notarytool"], ct);
        if (notarytool.Success) report.Add(Status.Ok, "Xcode tools", "codesign, notarytool, stapler");
        else report.Add(Status.Warn, "Xcode tools", "notarytool not found (needed to notarize)", "Install Xcode, or the command line tools: xcode-select --install");

        var identities = await SigningIdentity.FindAllAsync(ct);
        var developerIds = identities.Where(i => i.IsDeveloperId).ToList();
        var teamId = developerIds.Select(i => i.TeamId).FirstOrDefault(t => t != null);
        if (developerIds.Count > 0)
        {
            report.Add(Status.Ok, "Signing identity", string.Join('\n', developerIds.Select(i => i.Name)));
        }
        else
        {
            var detail = identities.Count > 0
                ? $"no Developer ID Application, only {string.Join(", ", identities.Select(i => i.Name.Split(':')[0]).Distinct())}"
                : "none in your keychain";
            report.Add(Status.Warn, "Signing identity", detail,
                "Create a Developer ID Application certificate (only the team's Account Holder can):\n" +
                "Xcode → Settings → Accounts → your team → Manage Certificates → + → Developer ID Application");
        }

        const string storeCredentials = "Make an app-specific password at account.apple.com (Sign-In and Security → App-Specific Passwords), then:";
        var storeCommand = $"xcrun notarytool store-credentials <profile> --apple-id <your Apple ID> --team-id {teamId ?? "<TEAMID>"}";
        if (notaryProfile == null)
        {
            report.Add(Status.Info, "Notary profile", "not checked (pass --notary-profile <name>)");
            if (developerIds.Count > 0) report.Fixes.Add($"If you haven't saved notarization credentials yet: {storeCredentials}\n{storeCommand}");
        }
        else
        {
            var history = await Run.Query("xcrun", ["notarytool", "history", "--keychain-profile", notaryProfile], ct);
            if (history.Success) report.Add(Status.Ok, "Notary profile", $"'{notaryProfile}' works");
            else report.Add(Status.Fail, "Notary profile", $"'{notaryProfile}': {FirstLine(history.Combined)}", $"{storeCredentials}\n{storeCommand}");
        }
    }

    static async Task CheckProjectAsync(Report report, string? projectPath, CancellationToken ct)
    {
        GameProject project;
        try
        {
            project = GameProject.Locate(projectPath, Environment.CurrentDirectory);
        }
        catch (ZincException e)
        {
            report.Add(Status.Info, "Game project", e.Message);
            return;
        }

        string[] names = ["AssemblyName", "Version", "ZincPublishTargets", "ZincAppName", "ZincBundleId", "ZincAppIcon"];
        Dictionary<string, string> props;
        try
        {
            props = await project.GetPropertiesAsync(names, ct);
        }
        catch (ZincException e)
        {
            report.Add(Status.Fail, "Game project", $"{Ui.Path(project.ProjectPath)}: {e.Message}");
            return;
        }

        report.Add(Status.Ok, "Game project", $"{Ui.Path(project.ProjectPath)} ({props["AssemblyName"]} {props["Version"]})");

        if (props["ZincPublishTargets"] == "true")
        {
            report.Add(Status.Ok, "Publish targets", "Zinc.Publish.targets imported");
        }
        else
        {
            var zinc = project.ZincProjectPath;
            var import = zinc == null ? "Zinc.Publish.targets" : Path.GetRelativePath(project.ProjectDirectory, Path.Combine(Path.GetDirectoryName(zinc)!, "Zinc.Publish.targets"));
            report.Add(Status.Fail, "Publish targets", "Zinc.Publish.targets isn't imported, so zinc can't package",
                $"Add to {Path.GetFileName(project.ProjectPath)}, after the Zinc ProjectReference:\n<Import Project=\"{import.Replace('/', '\\')}\" />\n(If Zinc has no such file, update the Zinc submodule.)");
        }

        var appName = props["ZincAppName"] is { Length: > 0 } n ? n : props["AssemblyName"];
        report.Add(Status.Info, "App name", appName);
        if (props["ZincBundleId"].Length > 0) report.Add(Status.Ok, "Bundle ID", props["ZincBundleId"]);
        else report.Add(Status.Warn, "Bundle ID", "not set (needed to sign)", $"Set one in {Path.GetFileName(project.ProjectPath)}: <ZincBundleId>com.yourstudio.{appName.ToLowerInvariant()}</ZincBundleId>");

        if (props["ZincAppIcon"].Length > 0) report.Add(Status.Ok, "App icon", props["ZincAppIcon"]);
        else report.Add(Status.Info, "App icon", "not set, packages get the default icon (<ZincAppIcon>res/icon.png</ZincAppIcon>)");

        var toolManifest = ToolManifest.Find(project.RootDirectory);
        if (toolManifest != null && File.ReadAllText(toolManifest).Contains("\"zinc\""))
            report.Add(Status.Ok, "zinc pinned", $"in {Path.GetRelativePath(project.RootDirectory, toolManifest)}");
        else
            report.Add(Status.Info, "zinc pinned", $"no (pin it: dotnet tool install zinc --local --version {ToolInfo.Version})");
    }

    static string FirstLine(string text) => text.Trim().Split('\n').FirstOrDefault()?.Trim() ?? "";
}
