using System.Text.Json;

namespace Zinc.Cli;

/// <summary>
/// zinc.publish.json, which Zinc.Publish.targets writes into every RID-specific publish. It's the
/// contract between Zinc and this tool: what was built, which folders hold native code (everything
/// else is content), and the game's packaging settings. The tool reads it rather than assuming
/// Zinc's layout, so the two can be versioned separately.
/// </summary>
sealed record PublishManifest(
    int Schema,
    string RuntimeIdentifier,
    string Configuration,
    bool SelfContained,
    bool SingleFile,
    string Version,
    string Executable,
    string[] Native,
    string AppName,
    string BundleId,
    string Icon)
{
    public const string FileName = "zinc.publish.json";
    const int SupportedSchema = 1;

    static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static bool ExistsIn(string publishDirectory) => File.Exists(Path.Combine(publishDirectory, FileName));

    public static PublishManifest Load(string publishDirectory)
    {
        var path = Path.Combine(publishDirectory, FileName);
        if (!File.Exists(path))
            throw new ZincException($"{Ui.Path(publishDirectory)} has no {FileName}, so zinc can't tell what was built there.",
                "Point --from at a folder published with Zinc.Publish.targets imported, or leave out --from and let zinc publish.");

        PublishManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<PublishManifest>(File.ReadAllText(path), Options);
        }
        catch (JsonException e)
        {
            throw new ZincException($"{Ui.Path(path)} isn't valid JSON: {e.Message}",
                "A packaging property (ZincAppName, ZincBundleId, ZincAppIcon) probably contains a quote or backslash.");
        }

        if (manifest == null) throw new ZincException($"{Ui.Path(path)} is empty.");
        if (manifest.Schema > SupportedSchema)
            throw new ZincException($"{Ui.Path(path)} was written by a newer Zinc (schema {manifest.Schema}) than this zinc understands.",
                "Update the zinc tool.");
        return manifest;
    }

    /// <summary>Refuses builds that shouldn't be shipped as <paramref name="platform"/>.</summary>
    public void RequireReleaseFor(string platform, string ridPrefix)
    {
        if (!RuntimeIdentifier.StartsWith(ridPrefix, StringComparison.OrdinalIgnoreCase))
            throw new ZincException($"This publish is for {RuntimeIdentifier}, not {platform}.");
        if (!Configuration.Equals("Release", StringComparison.OrdinalIgnoreCase))
            throw new ZincException($"This publish is a {Configuration} build; zinc only packages Release builds.");
        if (!SelfContained)
            Ui.Warn("This publish isn't self-contained, so players will need the .NET runtime installed.");
    }
}
