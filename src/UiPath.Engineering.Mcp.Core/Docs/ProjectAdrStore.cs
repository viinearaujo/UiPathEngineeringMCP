using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using UiPath.Engineering.Mcp.Core.Abstractions;

namespace UiPath.Engineering.Mcp.Core.Docs;

public sealed class ProjectAdrStore {
    public const string Kind = "adr";
    private static readonly Regex SlugPattern = new(@"[^a-z0-9]+", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly IFilesystemProvider _filesystem;

    public ProjectAdrStore(IFilesystemProvider filesystem) => _filesystem = filesystem;

    public AdrIndex Load(string projectPath) {
        var path = ProjectDocsPaths.AdrIndex(projectPath);
        if (!_filesystem.FileExists(path)) {
            return new AdrIndex();
        }

        try {
            return JsonSerializer.Deserialize<AdrIndex>(_filesystem.ReadAllText(path), JsonOptions)
                ?? new AdrIndex();
        } catch (JsonException) {
            return new AdrIndex();
        }
    }

    public AdrRecord Write(string projectPath, string title, string content, IReadOnlyList<string>? relatedFiles, string? status, string? supersedes, string? id = null) {
        if (string.IsNullOrWhiteSpace(title)) {
            throw new ArgumentException("title is required.", nameof(title));
        }

        if (!HasRequiredSections(content)) {
            throw new ArgumentException("ADR content must include Context, Decision, and Consequences sections.");
        }

        var normalizedStatus = string.IsNullOrWhiteSpace(status) ? AdrRecord.Proposed : status.Trim().ToLowerInvariant();
        if (normalizedStatus is not (AdrRecord.Proposed or AdrRecord.Accepted or AdrRecord.Superseded or AdrRecord.Deprecated)) {
            throw new ArgumentException("status must be proposed, accepted, superseded, or deprecated.", nameof(status));
        }

        var index = Load(projectPath);
        AdrRecord record;
        if (!string.IsNullOrWhiteSpace(id)) {
            record = index.Adrs.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"ADR '{id}' was not found.", nameof(id));
            record.Status = normalizedStatus;
            record.UpdatedUtc = DateTimeOffset.UtcNow;
            record.RelatedFiles.Clear();
            if (relatedFiles is not null) {
                record.RelatedFiles.AddRange(relatedFiles);
            }

            if (supersedes is not null) {
                record.Supersedes = supersedes;
            }

            _filesystem.WriteAllText(ProjectDocsPaths.AdrFile(projectPath, record.FileName), content);
        } else {
            var number = index.Adrs.Count == 0 ? 1 : index.Adrs.Max(a => a.Number) + 1;
            var slug = ToSlug(title);
            var fileName = $"{number:D4}-{slug}.md";
            var newId = Path.GetFileNameWithoutExtension(fileName);
            record = new AdrRecord {
                Id = newId,
                Number = number,
                Title = title,
                FileName = fileName,
                Status = normalizedStatus,
                RelatedFiles = relatedFiles?.ToList() ?? [],
                UpdatedUtc = DateTimeOffset.UtcNow,
                Supersedes = string.IsNullOrWhiteSpace(supersedes) ? null : supersedes
            };
            index.Adrs.Add(record);
            _filesystem.CreateDirectory(ProjectDocsPaths.AdrDir(projectPath));
            _filesystem.WriteAllText(ProjectDocsPaths.AdrFile(projectPath, fileName), content);
        }

        if (!string.IsNullOrWhiteSpace(record.Supersedes)) {
            MarkSuperseded(index, projectPath, record.Supersedes);
        }

        Save(projectPath, index);
        return record;
    }

    public static string RenderTemplate(string title, string status, string? context, string? decision, string? consequences) {
        var sb = new StringBuilder();
        sb.AppendLine($"# {title.Trim()}");
        sb.AppendLine();
        sb.AppendLine($"Date: {DateTime.UtcNow:yyyy-MM-dd}");
        sb.AppendLine();
        sb.AppendLine($"Status: {status}");
        sb.AppendLine();
        sb.AppendLine("## Context");
        sb.AppendLine();
        sb.AppendLine(string.IsNullOrWhiteSpace(context) ? Placeholder("context") : context.Trim());
        sb.AppendLine();
        sb.AppendLine("## Decision");
        sb.AppendLine();
        sb.AppendLine(string.IsNullOrWhiteSpace(decision) ? Placeholder("decision") : decision.Trim());
        sb.AppendLine();
        sb.AppendLine("## Consequences");
        sb.AppendLine();
        sb.AppendLine(string.IsNullOrWhiteSpace(consequences) ? Placeholder("consequences") : consequences.Trim());
        sb.AppendLine();
        return sb.ToString();
    }

    public static bool HasRequiredSections(string? content) {
        if (string.IsNullOrWhiteSpace(content)) {
            return false;
        }

        return HasHeading(content, "Context")
            && HasHeading(content, "Decision")
            && HasHeading(content, "Consequences");
    }

    public bool Delete(string projectPath, string id) {
        var index = Load(projectPath);
        var record = index.Adrs.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
        if (record is null) {
            return false;
        }

        index.Adrs.Remove(record);
        var path = ProjectDocsPaths.AdrFile(projectPath, record.FileName);
        if (_filesystem.FileExists(path)) {
            _filesystem.DeleteFile(path);
        }

        Save(projectPath, index);
        return true;
    }

    public IReadOnlyList<string> ListMarkdownFiles(string projectPath) {
        var dir = ProjectDocsPaths.AdrDir(projectPath);
        var tree = _filesystem.GetDirectoryTree(dir, maxDepth: 1);
        return tree.Children
            .Where(c => !c.IsDirectory && c.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Path)
            .ToList();
    }

    public static string ToSlug(string title) {
        var slug = SlugPattern.Replace(title.Trim().ToLowerInvariant(), "-").Trim('-');
        return string.IsNullOrEmpty(slug) ? "untitled" : slug;
    }

    private void MarkSuperseded(AdrIndex index, string projectPath, string supersededId) {
        var old = index.Adrs.FirstOrDefault(a => string.Equals(a.Id, supersededId, StringComparison.OrdinalIgnoreCase));
        if (old is null) {
            return;
        }

        old.Status = AdrRecord.Superseded;
        old.UpdatedUtc = DateTimeOffset.UtcNow;
        var path = ProjectDocsPaths.AdrFile(projectPath, old.FileName);
        if (!_filesystem.FileExists(path)) {
            return;
        }

        var markdown = _filesystem.ReadAllText(path);
        var updated = new Regex(@"(?im)^Status:\s*.+$").Replace(markdown, $"Status: {AdrRecord.Superseded}", 1);
        _filesystem.WriteAllText(path, updated);
    }

    private static bool HasHeading(string content, string heading) =>
        Regex.IsMatch(content, $@"^#+\s*{Regex.Escape(heading)}\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline);

    private static string Placeholder(string section) => $"({section} goes here)";

    private void Save(string projectPath, AdrIndex index) {
        _filesystem.CreateDirectory(ProjectDocsPaths.AdrDir(projectPath));
        _filesystem.WriteAllText(ProjectDocsPaths.AdrIndex(projectPath), JsonSerializer.Serialize(index, JsonOptions));
    }
}
