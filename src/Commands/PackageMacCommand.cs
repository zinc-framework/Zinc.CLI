using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Zinc.Cli.Packaging;

namespace Zinc.Cli.Commands;

/// <summary>Options every `zinc package &lt;platform&gt;` command takes.</summary>
class PackageSettings : CommandSettings
{
    [CommandOption("--project <PATH>")]
    [Description("The game's .csproj, or its folder. Found automatically when the repository has one.")]
    public string? Project { get; set; }

    [CommandOption("--from <DIR>")]
    [Description("Package this existing publish folder instead of publishing. It must hold the zinc.publish.json Zinc writes.")]
    public string? From { get; set; }

    [CommandOption("-o|--output <DIR>")]
    [Description("Where the finished package goes. Defaults to dist/<platform> in the repository.")]
    public string? Output { get; set; }

    [CommandOption("-v|--verbose")]
    [Description("Show the output of every command zinc runs.")]
    public bool Verbose { get; set; }

    public override ValidationResult Validate() =>
        From != null && Project != null
            ? ValidationResult.Error("--from packages a folder that's already published, so it can't be combined with --project.")
            : ValidationResult.Success();
}

sealed class PackageMacSettings : PackageSettings
{
    [CommandOption("--sign [IDENTITY]")]
    [Description("Sign for distribution with a Developer ID. With no value, uses the one in your keychain.")]
    public FlagValue<string> Sign { get; set; } = new();

    [CommandOption("--notarize <PROFILE>")]
    [Description("Notarize and staple, using a keychain profile saved with `xcrun notarytool store-credentials`. Needs --sign.")]
    public string? Notarize { get; set; }

    public override ValidationResult Validate()
    {
        if (Notarize != null && !Sign.IsSet) return ValidationResult.Error("--notarize needs --sign: Apple only notarizes signed apps.");
        return base.Validate();
    }
}

[Description("Publish the game and package it as a macOS .app, optionally signed and notarized.")]
sealed class PackageMacCommand : AsyncCommand<PackageMacSettings>
{
    const string Rid = "osx-arm64";

    public override async Task<int> ExecuteAsync(CommandContext context, PackageMacSettings settings, CancellationToken ct)
    {
        Run.Verbose = settings.Verbose;
        if (!OperatingSystem.IsMacOS())
            throw new ZincException("Packaging for macOS needs a Mac: the bundle is signed with Apple's codesign.");

        // Everything after -- goes to dotnet publish.
        var publishArgs = context.Remaining.Raw.ToList();

        // Resolve the signing identity first, so a missing certificate fails before a long publish.
        var identity = settings.Sign.IsSet ? await SigningIdentity.ResolveAsync(settings.Sign.Value, ct) : null;
        if (settings.Notarize != null && !identity!.IsDeveloperId)
            throw new ZincException($"Apple only notarizes apps signed with a 'Developer ID Application' identity, not '{identity.Name}'.");

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
            // Single-file puts every managed assembly inside the executable. Otherwise .NET's satellite
            // assembly folders would sit in Contents/MacOS, and codesign rejects non-code files in there.
            publishDir = await Publisher.PublishAsync(project, Rid, ["-p:PublishSingleFile=true"], publishArgs, ct);
        }

        var manifest = PublishManifest.Load(publishDir);
        manifest.RequireReleaseFor("macOS", "osx");
        if (!manifest.SingleFile)
            throw new ZincException("macOS packages need a single-file publish: codesign rejects .NET's loose assembly folders inside a .app.",
                settings.From != null ? "Publish with -p:PublishSingleFile=true, or leave out --from and let zinc publish." : null);
        if (identity != null && string.IsNullOrEmpty(manifest.BundleId))
            throw new ZincException("Signing needs a bundle identifier.",
                "Set one in the game's .csproj, e.g. <ZincBundleId>com.yourstudio.yourgame</ZincBundleId>");

        var outputDir = Path.GetFullPath(settings.Output ?? Path.Combine(root, "dist", "macos"));
        var workDir = Path.Combine(Publisher.StagingDirectory(root, manifest.RuntimeIdentifier), "work");
        Directory.CreateDirectory(outputDir);
        Directory.CreateDirectory(workDir);

        var app = await MacBundler.AssembleAsync(manifest, publishDir, outputDir, workDir, ct);
        await MacBundler.SignAsync(app, manifest, identity, workDir, ct);

        string? zip = null;
        if (identity != null)
        {
            zip = await MacBundler.ZipAsync(app, manifest, ct);
            if (settings.Notarize != null)
            {
                await MacBundler.NotarizeAsync(app, zip, settings.Notarize, ct);
                // Re-zip so the upload carries the stapled ticket.
                zip = await MacBundler.ZipAsync(app, manifest, ct);
            }
        }

        Summarize(manifest, app, zip, identity, notarized: settings.Notarize != null);
        return 0;
    }

    static void Summarize(PublishManifest manifest, string app, string? zip, SigningIdentity? identity, bool notarized)
    {
        Ui.Blank();
        var table = new Table().Border(TableBorder.Rounded).AddColumns("Package", "Size", "Path");
        table.AddRow(Markup.Escape(Path.GetFileName(app)), Files.Size(app), Markup.Escape(Ui.Path(app)));
        if (zip != null) table.AddRow(Markup.Escape(Path.GetFileName(zip)), Files.Size(zip), Markup.Escape(Ui.Path(zip)));
        AnsiConsole.Write(table);

        if (string.IsNullOrEmpty(manifest.BundleId))
            Ui.Hint($"No <ZincBundleId> set, so it's using {MacBundler.LocalBundleId(manifest.AppName)}. Set one before signing.");

        if (identity == null)
        {
            Ui.Warn("Ad-hoc signed: runs on this Mac, but Gatekeeper blocks it on others.");
            Ui.Hint("To ship it: zinc package mac --sign --notarize <profile>   ('zinc doctor' covers the setup)");
        }
        else if (!notarized)
        {
            Ui.Warn("Signed but not notarized: other Macs will still refuse it on first launch.");
            Ui.Hint("Add --notarize <profile> to finish.");
        }
        else
        {
            Ui.Success($"Signed, notarized and stapled. Upload {Ui.Path(zip!)}.");
        }
    }
}
