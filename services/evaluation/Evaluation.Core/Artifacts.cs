namespace Evaluation.Core;

public static class Artifacts
{
    public const int MaximumDtFiles = 300;
    // A Source evidence upload (folder or multi-file) and the evidence attached to one scan share this bound.
    public const int MaximumSourceFiles = 300;
    public const int MaximumPathLength = 260;
    public const int MaximumArtifactCharacters = 5_000_000;
    public static readonly string[] SupportedExtensions = [".pdf", ".docx", ".md", ".txt"];

    // Normalizes a browser-supplied relative path. Rejects absolute, drive, '.', '..' and empty segments.
    public static string NormalizePath(string? path, bool markdownOnly)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > MaximumPathLength || path.Any(char.IsControl))
            throw new ArgumentException($"File paths must contain 1-{MaximumPathLength} printable characters.");
        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Contains(':') ||
            normalized.Split('/').Any(s => s.Length == 0 || s == "." || s == ".." || s.Trim().Length != s.Length))
            throw new ArgumentException("File paths must be relative without '.', '..', drive letters, empty or padded segments.");
        if (markdownOnly && !normalized.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("DT bundles accept Markdown (.md) files only.");
        return normalized;
    }

    // Groups selected target versions into at most one artifact per role. A DT artifact must be one complete bundle.
    public static ArtifactSelection[] Group(IReadOnlyCollection<InputSnapshot> targets, IReadOnlyCollection<(Guid Id, Guid BundleId)> bundleMembers)
    {
        if (targets.Count == 0) throw new ArgumentException("Select at least one DT bundle, BRD or PRD version to scan.");
        if (targets.Any(t => t.Role == DocumentRole.Source)) throw new ArgumentException("Source documents are context only and cannot be scan targets.");
        var result = new List<ArtifactSelection>();
        foreach (var role in new[] { DocumentRole.DT, DocumentRole.BRD, DocumentRole.PRD })
        {
            var selected = targets.Where(t => t.Role == role).ToArray();
            if (selected.Length == 0) continue;
            if (role == DocumentRole.DT)
            {
                var bundles = selected.Select(t => t.BundleId).Distinct().ToArray();
                if (bundles.Length != 1) throw new ArgumentException("Select exactly one DT bundle.");
                var members = bundleMembers.Where(m => m.BundleId == bundles[0]).Select(m => m.Id).ToHashSet();
                if (!members.SetEquals(selected.Select(t => t.VersionId)) || selected.Length > MaximumDtFiles)
                    throw new ArgumentException("A DT scan must include every file of one uploaded bundle.");
            }
            else if (selected.Length != 1) throw new ArgumentException($"Select at most one {role} version.");
            if (selected.Sum(t => (long)t.TextLength) > MaximumArtifactCharacters)
                throw new ArgumentException($"The selected {role} text exceeds the judge limit of {MaximumArtifactCharacters:N0} characters.");
            result.Add(new(role, selected.OrderBy(t => t.RelativePath, StringComparer.Ordinal).Select(t => t.VersionId).ToArray()));
        }
        return result.ToArray();
    }
}
