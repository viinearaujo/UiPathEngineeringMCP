using System.Text.Json;
using System.Text.RegularExpressions;
using UiPath.Engineering.Mcp.Core.Abstractions;

namespace UiPath.Engineering.Mcp.Core.Docs;

public sealed class ProjectKnowledgeStore {
    public const string Kind = "memory";
    private static readonly Regex IdPattern = new(@"^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly IFilesystemProvider _filesystem;

    public ProjectKnowledgeStore(IFilesystemProvider filesystem) => _filesystem = filesystem;

    public static bool IsValidId(string id) =>
        !string.IsNullOrWhiteSpace(id) && IdPattern.IsMatch(id);

    public KnowledgeIndex Load(string projectPath) {
        var path = ProjectDocsPaths.KnowledgeIndex(projectPath);
        if (!_filesystem.FileExists(path)) {
            return new KnowledgeIndex();
        }

        try {
            return JsonSerializer.Deserialize<KnowledgeIndex>(_filesystem.ReadAllText(path), JsonOptions)
                ?? new KnowledgeIndex();
        } catch (JsonException) {
            return new KnowledgeIndex();
        }
    }

    public KnowledgeArticle Upsert(string projectPath, string id, string title, string content, IReadOnlyList<string>? relatedFiles, string? status) {
        if (!IsValidId(id)) {
            throw new ArgumentException("id must be kebab-case [a-z0-9-]+.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(title)) {
            throw new ArgumentException("title is required.", nameof(title));
        }

        var normalizedStatus = string.IsNullOrWhiteSpace(status) ? KnowledgeArticle.Current : status.Trim().ToLowerInvariant();
        if (normalizedStatus is not (KnowledgeArticle.Current or KnowledgeArticle.Deprecated)) {
            throw new ArgumentException("status must be current or deprecated.", nameof(status));
        }

        var index = Load(projectPath);
        var article = index.Articles.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
        if (article is null) {
            article = new KnowledgeArticle { Id = id, FileName = id + ".md" };
            index.Articles.Add(article);
        }

        article.Title = title.Trim();
        article.Status = normalizedStatus;
        article.UpdatedUtc = DateTimeOffset.UtcNow;
        article.RelatedFiles.Clear();
        if (relatedFiles is not null) {
            article.RelatedFiles.AddRange(relatedFiles);
        }

        _filesystem.CreateDirectory(ProjectDocsPaths.KnowledgeDir(projectPath));
        _filesystem.WriteAllText(ProjectDocsPaths.KnowledgeArticle(projectPath, id), content ?? string.Empty);
        Save(projectPath, index);
        return article;
    }

    public bool Delete(string projectPath, string id) {
        var index = Load(projectPath);
        var removed = index.Articles.RemoveAll(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
        var articlePath = ProjectDocsPaths.KnowledgeArticle(projectPath, id);
        var existed = _filesystem.FileExists(articlePath);
        if (existed) {
            _filesystem.DeleteFile(articlePath);
        }

        if (removed > 0) {
            Save(projectPath, index);
        }

        return removed > 0 || existed;
    }

    public IReadOnlyList<string> ListMarkdownFiles(string projectPath) {
        var dir = ProjectDocsPaths.KnowledgeDir(projectPath);
        var tree = _filesystem.GetDirectoryTree(dir, maxDepth: 1);
        return tree.Children
            .Where(c => !c.IsDirectory && c.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Path)
            .ToList();
    }

    private void Save(string projectPath, KnowledgeIndex index) {
        _filesystem.CreateDirectory(ProjectDocsPaths.KnowledgeDir(projectPath));
        _filesystem.WriteAllText(ProjectDocsPaths.KnowledgeIndex(projectPath), JsonSerializer.Serialize(index, JsonOptions));
    }
}
