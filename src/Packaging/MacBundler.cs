using System.Security;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Zinc.Cli.Packaging;

/// <summary>
/// Turns a macOS publish into a .app bundle, then signs, zips and notarizes it.
///
/// Layout: the executable and native code go in Contents/MacOS, everything else in
/// Contents/Resources. codesign refuses to sign a bundle with non-code files in Contents/MacOS, and
/// Zinc looks for content in Contents/Resources when it runs from a bundle.
/// </summary>
static partial class MacBundler
{
    // The hardened runtime (required for notarization) blocks JIT by default; .NET needs it.
    const string Entitlements = """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
            <key>com.apple.security.cs.allow-jit</key>
            <true/>
        </dict>
        </plist>
        """;

    public static async Task<string> AssembleAsync(PublishManifest manifest, string publishDir, string outputDir, string workDir, CancellationToken ct)
    {
        var app = Path.Combine(outputDir, $"{manifest.AppName}.app");
        Ui.Step($"Assembling {Ui.Path(app)}");

        Files.DeleteDirectory(app);
        var contents = Path.Combine(app, "Contents");
        var macOS = Directory.CreateDirectory(Path.Combine(contents, "MacOS")).FullName;
        var resources = Directory.CreateDirectory(Path.Combine(contents, "Resources")).FullName;

        if (!File.Exists(Path.Combine(publishDir, manifest.Executable)))
            throw new ZincException($"The publish has no executable '{manifest.Executable}'.");

        foreach (var entry in Directory.EnumerateFileSystemEntries(publishDir))
        {
            var name = Path.GetFileName(entry);
            if (name is PublishManifest.FileName or ".DS_Store") continue;
            Files.Copy(entry, Path.Combine(IsCode(entry, manifest) ? macOS : resources, name));
        }

        var hasIcon = await BuildIconAsync(manifest, publishDir, resources, workDir, ct);
        await File.WriteAllTextAsync(Path.Combine(contents, "Info.plist"), InfoPlist(manifest, hasIcon), ct);
        return app;
    }

    /// <summary>Ad-hoc signs when <paramref name="identity"/> is null: valid, but only trusted on this Mac.</summary>
    public static async Task SignAsync(string app, PublishManifest manifest, SigningIdentity? identity, string workDir, CancellationToken ct)
    {
        Ui.Step(identity == null ? "Signing ad-hoc" : $"Signing as {identity.Name}");

        // Inside out: nested code first, then the bundle, which signs the main executable and seals the rest.
        var macOS = Path.Combine(app, "Contents", "MacOS");
        var executable = Path.Combine(macOS, manifest.Executable);
        var nested = Directory.EnumerateFiles(macOS, "*", SearchOption.AllDirectories)
            .Where(f => f != executable && IsMachO(f))
            .ToList();

        string[] common = identity == null
            ? ["--force", "--sign", "-"]
            : ["--force", "--timestamp", "--options", "runtime", "--sign", identity.Hash];

        if (nested.Count > 0)
            await Run.Tool("codesign", [.. common, .. nested], "Signing native libraries", ct);

        if (identity == null)
        {
            await Run.Tool("codesign", [.. common, app], "Signing the app", ct);
        }
        else
        {
            var entitlements = Path.Combine(workDir, "entitlements.plist");
            await File.WriteAllTextAsync(entitlements, Entitlements, ct);
            await Run.Tool("codesign", [.. common, "--entitlements", entitlements, app], "Signing the app", ct);
        }

        await Run.Tool("codesign", ["--verify", "--deep", "--strict", app], "Verifying the signature", ct);
    }

    /// <summary>Zips with ditto, which keeps the bundle's symlinks, permissions and extended attributes intact.</summary>
    public static async Task<string> ZipAsync(string app, PublishManifest manifest, CancellationToken ct)
    {
        var zip = Path.Combine(Path.GetDirectoryName(app)!, $"{manifest.AppName}-macos.zip");
        File.Delete(zip);
        await Run.Tool("ditto", ["-c", "-k", "--keepParent", app, zip], "Zipping", ct);
        return zip;
    }

    /// <summary>Submits to Apple's notary service, waits, and staples the ticket so the app verifies offline.</summary>
    public static async Task NotarizeAsync(string app, string zip, string profile, CancellationToken ct)
    {
        Ui.Step("Notarizing");
        var submit = await Run.Tool("xcrun",
            ["notarytool", "submit", zip, "--keychain-profile", profile, "--wait", "--output-format", "json"],
            "Waiting on Apple's notary service (usually a few minutes)", ct, allowFailure: true);

        var (id, status, message) = ParseSubmission(submit);
        if (status != "Accepted")
        {
            if (id != null)
            {
                // The log says which file failed and why.
                var log = await Run.Query("xcrun", ["notarytool", "log", id, "--keychain-profile", profile], ct);
                foreach (var line in log.Combined.TrimEnd().Split('\n')) Ui.Output(line);
            }
            else
            {
                foreach (var line in submit.Combined.TrimEnd().Split('\n')) Ui.Output(line);
            }
            throw new ZincException($"Notarization {(status ?? "failed").ToLowerInvariant()}{(message != null ? $": {message}" : ".")}",
                id == null ? $"Check the keychain profile with: zinc doctor --notary-profile {profile}" : null);
        }
        Ui.Success($"Notarized (submission {id})");

        await Run.Tool("xcrun", ["stapler", "staple", app], "Stapling the ticket", ct);

        var assess = await Run.Query("spctl", ["--assess", "--type", "execute", "--verbose", app], ct);
        if (assess.Success) Ui.Success($"Gatekeeper: {assess.Combined.Trim().Split('\n').Last().Trim()}");
        else Ui.Warn($"Gatekeeper rejected the stapled app: {assess.Combined.Trim()}");
    }

    static (string? Id, string? Status, string? Message) ParseSubmission(RunResult result)
    {
        var json = result.StdOut;
        var start = json.IndexOf('{');
        var end = json.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            try
            {
                var root = JsonDocument.Parse(json[start..(end + 1)]).RootElement;
                string? Get(string name) => root.TryGetProperty(name, out var v) ? v.GetString() : null;
                return (Get("id"), Get("status"), Get("message"));
            }
            catch (JsonException) { }
        }
        return (null, null, null);
    }

    static async Task<bool> BuildIconAsync(PublishManifest manifest, string publishDir, string resources, string workDir, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(manifest.Icon)) return false;
        var source = Path.Combine(publishDir, manifest.Icon);
        if (!File.Exists(source))
        {
            Ui.Warn($"ZincAppIcon '{manifest.Icon}' isn't in the publish, so the app gets the default icon.");
            return false;
        }

        var iconset = Path.Combine(workDir, "AppIcon.iconset");
        Files.DeleteDirectory(iconset);
        Directory.CreateDirectory(iconset);
        foreach (var size in new[] { 16, 32, 128, 256, 512 })
        {
            await Sips(size, $"icon_{size}x{size}.png");
            await Sips(size * 2, $"icon_{size}x{size}@2x.png");
        }

        var icns = await Run.Query("iconutil", ["-c", "icns", iconset, "-o", Path.Combine(resources, "AppIcon.icns")], ct);
        if (!icns.Success) throw new ZincException($"iconutil couldn't build the icon: {icns.Combined.Trim()}");
        return true;

        async Task Sips(int pixels, string name)
        {
            var result = await Run.Query("sips", ["-z", $"{pixels}", $"{pixels}", source, "--out", Path.Combine(iconset, name)], ct);
            if (!result.Success) throw new ZincException($"sips couldn't resize {manifest.Icon}: {result.Combined.Trim()}");
        }
    }

    static string InfoPlist(PublishManifest manifest, bool hasIcon)
    {
        var bundleId = string.IsNullOrEmpty(manifest.BundleId) ? LocalBundleId(manifest.AppName) : manifest.BundleId;
        // CFBundleShortVersionString must be up to three dot-separated integers: drop any prerelease or build suffix.
        var version = LeadingVersion().Match(manifest.Version) is { Success: true } m ? m.Value : "1.0";

        List<string> lines =
        [
            """<?xml version="1.0" encoding="UTF-8"?>""",
            """<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">""",
            """<plist version="1.0">""",
            "<dict>",
        ];
        void Add(string key, string value) => lines.AddRange([$"    <key>{key}</key>", $"    <string>{SecurityElement.Escape(value)}</string>"]);

        Add("CFBundleExecutable", manifest.Executable);
        Add("CFBundleIdentifier", bundleId);
        Add("CFBundleName", manifest.AppName);
        Add("CFBundleDisplayName", manifest.AppName);
        if (hasIcon) Add("CFBundleIconFile", "AppIcon");
        Add("CFBundlePackageType", "APPL");
        Add("CFBundleShortVersionString", version);
        Add("CFBundleVersion", version);
        lines.AddRange(["    <key>NSHighResolutionCapable</key>", "    <true/>", "</dict>", "</plist>", ""]);
        return string.Join('\n', lines);
    }

    /// <summary>A placeholder for unsigned builds of games that haven't set ZincBundleId.</summary>
    public static string LocalBundleId(string appName) => $"local.zinc.{InvalidBundleIdChars().Replace(appName, "-")}";

    /// <summary>
    /// Code runs from Contents/MacOS: the executable, the folders Zinc lists as native, and any other
    /// native library published next to the executable (from a NuGet package, say), since that's
    /// where .NET looks for them. Everything else is content.
    /// </summary>
    static bool IsCode(string path, PublishManifest manifest)
    {
        var name = Path.GetFileName(path);
        return name == manifest.Executable || manifest.Native.Contains(name) || (File.Exists(path) && IsMachO(path));
    }

    static bool IsMachO(string path)
    {
        Span<byte> magic = stackalloc byte[4];
        using var file = File.OpenRead(path);
        if (file.Read(magic) < 4) return false;
        var value = BitConverter.ToUInt32(magic);
        // 64/32-bit, either byte order, plus universal (fat) binaries.
        return value is 0xFEEDFACF or 0xCFFAEDFE or 0xFEEDFACE or 0xCEFAEDFE or 0xCAFEBABE or 0xBEBAFECA;
    }

    [GeneratedRegex(@"^\d+(\.\d+){0,2}")]
    private static partial Regex LeadingVersion();

    [GeneratedRegex(@"[^A-Za-z0-9.-]")]
    private static partial Regex InvalidBundleIdChars();
}
