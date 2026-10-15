using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Zinc.Cli.Packaging;

namespace Zinc.Cli.Commands;

[Description("Publish the game and package it as a Windows folder, plus a zip of it.")]
sealed class PackageWindowsCommand : AsyncCommand<PackageSettings>
{
    const string Rid = "win-x64";

    public override async Task<int> ExecuteAsync(CommandContext context, PackageSettings settings, CancellationToken ct)
    {
        Run.Verbose = settings.Verbose;
        if (!OperatingSystem.IsWindows())
            Ui.Warn("Not on Windows: the SDK only gives the .exe its icon and version info when it builds on Windows.");

        // Everything after -- goes to dotnet publish.
        var publishArgs = context.Remaining.Raw.ToList();

        string publishDir, root;
        if (settings.From != null)
        {
            if (publishArgs.Count > 0)
                throw new ZincException("Arguments after -- are for dotnet publish, which --from skips.");
            publishDir = Path.GetFullPath(settings.From);
            root = GameProject.FindRoot(Environment.CurrentDirectory);
        }
        else
        {
            var project = GameProject.Locate(settings.Project, Environment.CurrentDirectory);
            root = project.RootDirectory;
            var workDir = Path.Combine(Publisher.StagingDirectory(root, Rid), "work");
            Directory.CreateDirectory(workDir);

            // Single-file isn't required here as it is on macOS, but it leaves the folder players get
            // as the exe, the native libraries and the game's content, not a few hundred assemblies.
            // -- -p:PublishSingleFile=false turns it off.
            publishDir = await Publisher.PublishAsync(project, Rid,
                ["-p:PublishSingleFile=true", .. await WindowsIcon.PublishArgsAsync(project, workDir, ct)], publishArgs, ct);
        }

        var manifest = PublishManifest.Load(publishDir);
        manifest.RequireReleaseFor("Windows", "win");

        var outputDir = Path.GetFullPath(settings.Output ?? Path.Combine(root, "dist", "windows"));
        var folder = WindowsPackager.Assemble(manifest, publishDir, outputDir);
        var zip = WindowsPackager.Zip(folder, manifest);

        Summarize(folder, zip);
        return 0;
    }

    static void Summarize(string folder, string zip)
    {
        Ui.Blank();
        var table = new Table().Border(TableBorder.Rounded).AddColumns("Package", "Size", "Path");
        table.AddRow(Markup.Escape(Path.GetFileName(folder)), Files.Size(folder), Markup.Escape(Ui.Path(folder)));
        table.AddRow(Markup.Escape(Path.GetFileName(zip)), Files.Size(zip), Markup.Escape(Ui.Path(zip)));
        AnsiConsole.Write(table);

        Ui.Success($"Upload {Ui.Path(folder)} as a Steam depot, or the zip anywhere else.");
        Ui.Hint("It isn't code signed. Steam installs don't mind, but SmartScreen warns about an unsigned exe downloaded from the web.");
    }
}
