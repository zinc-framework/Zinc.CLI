using System.Text.RegularExpressions;

namespace Zinc.Cli.Packaging;

/// <summary>A code signing identity from the keychain, e.g. "Developer ID Application: Name (TEAMID)".</summary>
sealed partial record SigningIdentity(string Hash, string Name)
{
    /// <summary>The only kind Apple notarizes for distribution outside the App Store.</summary>
    public bool IsDeveloperId => Name.StartsWith("Developer ID Application:");

    public string? TeamId => TeamIdPattern().Match(Name) is { Success: true } m ? m.Groups[1].Value : null;

    public static async Task<List<SigningIdentity>> FindAllAsync(CancellationToken ct)
    {
        var result = await Run.Query("security", ["find-identity", "-v", "-p", "codesigning"], ct);
        return IdentityLine().Matches(result.StdOut)
            .Select(m => new SigningIdentity(m.Groups[1].Value, m.Groups[2].Value))
            .DistinctBy(i => i.Hash)
            .ToList();
    }

    /// <summary>
    /// Resolves what was passed to --sign. An empty value means "the Developer ID identity", which
    /// only works when there's exactly one. Signing uses the hash, since a renewed certificate
    /// leaves two identities with the same name and codesign rejects an ambiguous name.
    /// </summary>
    public static async Task<SigningIdentity> ResolveAsync(string? requested, CancellationToken ct)
    {
        var all = await FindAllAsync(ct);
        if (string.IsNullOrWhiteSpace(requested))
        {
            var developerIds = all.Where(i => i.IsDeveloperId).ToList();
            return developerIds.Count switch
            {
                1 => developerIds[0],
                0 => throw new ZincException("No 'Developer ID Application' signing identity in your keychain.",
                    "Run 'zinc doctor' for how to create one."),
                _ => throw new ZincException("Several 'Developer ID Application' identities; pick one with --sign <hash or name>:",
                    string.Join('\n', developerIds.Select(i => $"{i.Hash}  {i.Name}"))),
            };
        }

        var matches = all.Where(i => i.Hash.Equals(requested, StringComparison.OrdinalIgnoreCase) || i.Name == requested).ToList();
        return matches.Count switch
        {
            0 => throw new ZincException($"No signing identity '{requested}' in your keychain.", "Run 'zinc doctor' to list the ones you have."),
            // Several certificates with this name (usually a renewal): only the hash is unambiguous.
            > 1 => throw new ZincException($"'{requested}' matches several certificates; pass --sign with one of these hashes:",
                string.Join('\n', matches.Select(i => i.Hash))),
            _ => matches[0],
        };
    }

    [GeneratedRegex(@"^\s*\d+\)\s+([0-9A-F]{40})\s+""(.+)""", RegexOptions.Multiline)]
    private static partial Regex IdentityLine();

    [GeneratedRegex(@"\(([A-Z0-9]{10})\)$")]
    private static partial Regex TeamIdPattern();
}
