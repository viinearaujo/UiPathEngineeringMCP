using System.Text.RegularExpressions;

namespace UiPath.Engineering.Mcp.Providers.Git;

/// <summary>
/// Parses the output of `git status --porcelain=v1 --branch` into structured
/// branch/ahead/behind/changed-file data. The branch header looks like
/// "## main...origin/main [ahead 1, behind 2]" (or "## No commits yet on main");
/// every other line is an XY status entry whose path starts at column 3.
/// </summary>
public static class GitStatusParser
{
    private static readonly Regex AheadBehind = new(
        @"\[(?:ahead\s+(?<ahead>\d+))?(?:,\s*)?(?:behind\s+(?<behind>\d+))?\]",
        RegexOptions.Compiled);

    public static GitStatusResult Parse(string repoPath, string? stdOut)
    {
        var result = new GitStatusResult
        {
            RepoPath = repoPath,
            IsRepository = true
        };

        var changedFiles = new List<string>();
        var branch = string.Empty;
        var ahead = 0;
        var behind = 0;

        foreach (var line in SplitLines(stdOut))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                ParseBranchHeader(line.Substring(3), ref branch, ref ahead, ref behind);
                continue;
            }

            if (line.Length < 4)
            {
                continue;
            }

            // Porcelain entry: two status columns, a space, then the path.
            // Renames/copies use "old -> new"; keep the destination path.
            var path = line.Substring(3).Trim();
            var renameIndex = path.LastIndexOf(" -> ", StringComparison.Ordinal);
            if (renameIndex >= 0)
            {
                path = path.Substring(renameIndex + 4);
            }

            if (path.Length > 0)
            {
                changedFiles.Add(path);
            }
        }

        return new GitStatusResult
        {
            RepoPath = repoPath,
            IsRepository = true,
            Branch = branch,
            AheadCount = ahead,
            BehindCount = behind,
            ChangedFiles = changedFiles
        };
    }

    private static void ParseBranchHeader(string header, ref string branch, ref int ahead, ref int behind)
    {
        const string noCommitsPrefix = "No commits yet on ";
        if (header.StartsWith(noCommitsPrefix, StringComparison.Ordinal))
        {
            branch = header.Substring(noCommitsPrefix.Length).Trim();
            return;
        }

        var trackingIndex = header.IndexOf("...", StringComparison.Ordinal);
        var branchPart = trackingIndex >= 0 ? header.Substring(0, trackingIndex) : header;

        var bracketIndex = branchPart.IndexOf(" [", StringComparison.Ordinal);
        if (bracketIndex >= 0)
        {
            branchPart = branchPart.Substring(0, bracketIndex);
        }
        branch = branchPart.Trim();

        var match = AheadBehind.Match(header);
        if (match.Success)
        {
            if (int.TryParse(match.Groups["ahead"].Value, out var a))
            {
                ahead = a;
            }
            if (int.TryParse(match.Groups["behind"].Value, out var b))
            {
                behind = b;
            }
        }
    }

    private static IEnumerable<string> SplitLines(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
}
