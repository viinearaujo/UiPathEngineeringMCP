using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UiPath.Engineering.Mcp.Core.Configuration;

namespace UiPath.Engineering.Mcp.Providers.GitLab;

/// <summary>
/// GitLab REST API v4 client for a single configured project.
/// SECURITY: the access token is sent only via the PRIVATE-TOKEN header and is
/// never included in any return value, exception message, or log output.
/// HTTP failures surface as result errors containing only the status code and
/// a sanitized reason — never the response body, which could echo sensitive data.
/// </summary>
public sealed class GitLabProvider : IGitLabProvider {
    private readonly HttpClient _httpClient;
    private readonly GitLabOptions _options;
    private readonly ILogger<GitLabProvider> _logger;

    public GitLabProvider(
        HttpClient httpClient,
        IOptions<GitLabOptions> options,
        ILogger<GitLabProvider>? logger = null) {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger ?? NullLogger<GitLabProvider>.Instance;

        if (_options.TimeoutSeconds > 0) {
            _httpClient.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
        }
    }

    public async Task<GitLabIssueListResult> SearchIssuesAsync(string query, int maxResults, CancellationToken cancellationToken = default) {
        if (!IsConfigured(out var configError)) {
            _logger.LogInformation(
                "GitLab {Operation} duration {DurationMs}ms status {Status} errorCode {ErrorCode}",
                "SearchIssues", 0, "error", "not_configured");
            return new GitLabIssueListResult { Success = false, Errors = [configError] };
        }

        var clamped = Math.Clamp(maxResults, 1, 100);
        var url = $"{ProjectBaseUrl}/issues?search={Uri.EscapeDataString(query ?? string.Empty)}&per_page={clamped}";
        var sw = Stopwatch.StartNew();

        using var request = BuildRequest(HttpMethod.Get, url);
        try {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            sw.Stop();
            if (!response.IsSuccessStatusCode) {
                LogOutcome("SearchIssues", success: false, (int)response.StatusCode, sw.ElapsedMilliseconds, "http_error");
                return new GitLabIssueListResult { Success = false, Errors = [StatusError(response)] };
            }

            var issues = await response.Content.ReadFromJsonAsync<List<GitLabIssueSummary>>(cancellationToken: cancellationToken);
            LogOutcome("SearchIssues", success: true, (int)response.StatusCode, sw.ElapsedMilliseconds, errorCode: null);
            return new GitLabIssueListResult { Success = true, Issues = issues ?? [] };
        } catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) {
            sw.Stop();
            return RequestFailed("SearchIssues", ex, cancellationToken, sw.ElapsedMilliseconds,
                errors => new GitLabIssueListResult { Success = false, Errors = errors });
        }
    }

    public async Task<GitLabIssueResult> CreateIssueAsync(string title, string description, IReadOnlyList<string> labels, CancellationToken cancellationToken = default) {
        if (!IsConfigured(out var configError)) {
            _logger.LogInformation(
                "GitLab {Operation} duration {DurationMs}ms status {Status} errorCode {ErrorCode}",
                "CreateIssue", 0, "error", "not_configured");
            return new GitLabIssueResult { Success = false, Errors = [configError] };
        }

        var payload = new Dictionary<string, object?> {
            ["title"] = title,
            ["description"] = description
        };
        if (labels is { Count: > 0 }) {
            // GitLab accepts labels as a comma-separated string.
            payload["labels"] = string.Join(",", labels);
        }

        var sw = Stopwatch.StartNew();
        using var request = BuildRequest(HttpMethod.Post, $"{ProjectBaseUrl}/issues");
        request.Content = JsonContent.Create(payload);
        try {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            sw.Stop();
            if (!response.IsSuccessStatusCode) {
                LogOutcome("CreateIssue", success: false, (int)response.StatusCode, sw.ElapsedMilliseconds, "http_error");
                return new GitLabIssueResult { Success = false, Errors = [StatusError(response)] };
            }

            var issue = await response.Content.ReadFromJsonAsync<GitLabIssueSummary>(cancellationToken: cancellationToken);
            if (issue is null) {
                LogOutcome("CreateIssue", success: false, (int)response.StatusCode, sw.ElapsedMilliseconds, "empty_response");
                return new GitLabIssueResult { Success = false, Errors = ["GitLab returned an empty response."] };
            }

            LogOutcome("CreateIssue", success: true, (int)response.StatusCode, sw.ElapsedMilliseconds, errorCode: null);
            return new GitLabIssueResult { Success = true, Issue = issue };
        } catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) {
            sw.Stop();
            return RequestFailed("CreateIssue", ex, cancellationToken, sw.ElapsedMilliseconds,
                errors => new GitLabIssueResult { Success = false, Errors = errors });
        }
    }

    private string ProjectBaseUrl =>
        $"{_options.BaseUrl.TrimEnd('/')}/api/v4/projects/{Uri.EscapeDataString(_options.ProjectId)}";

    private bool IsConfigured(out string error) {
        if (string.IsNullOrWhiteSpace(_options.BaseUrl) || string.IsNullOrWhiteSpace(_options.ProjectId)) {
            error = "GitLab is not configured. Set GitLab:BaseUrl and GitLab:ProjectId in appsettings.json.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private HttpRequestMessage BuildRequest(HttpMethod method, string url) {
        var request = new HttpRequestMessage(method, url);
        if (!string.IsNullOrWhiteSpace(_options.AccessToken)) {
            // Header only — the token must never appear in results, errors, or logs.
            request.Headers.TryAddWithoutValidation("PRIVATE-TOKEN", _options.AccessToken);
        }
        return request;
    }

    // Only the numeric status code and the (framework-generated, token-free) reason
    // phrase are surfaced; the response body is deliberately never included.
    private static string StatusError(HttpResponseMessage response) =>
        $"GitLab request failed with status {(int)response.StatusCode} ({response.StatusCode}).";

    private void LogOutcome(string operation, bool success, int? httpStatus, long durationMs, string? errorCode) =>
        _logger.LogInformation(
            "GitLab {Operation} duration {DurationMs}ms status {Status} errorCode {ErrorCode} httpStatus {HttpStatus}",
            operation,
            durationMs,
            success ? "success" : "error",
            errorCode,
            httpStatus);

    private TResult RequestFailed<TResult>(
        string operation,
        Exception ex,
        CancellationToken cancellationToken,
        long durationMs,
        Func<List<string>, TResult> factory) {
        var errorCode = RequestErrorCode(ex, cancellationToken);
        _logger.LogWarning(
            ex,
            "GitLab {Operation} duration {DurationMs}ms status {Status} errorCode {ErrorCode} httpStatus {HttpStatus}",
            operation,
            durationMs,
            "error",
            errorCode,
            (int?)null);
        return factory([RequestError(ex, cancellationToken)]);
    }

    // Transport/parse failures have no HTTP status to report; never copy ex.Message
    // (proxy URLs, local paths, JSON snippets).
    private static string RequestError(Exception ex, CancellationToken cancellationToken) {
        if (ex is TaskCanceledException) {
            return cancellationToken.IsCancellationRequested
                ? "GitLab request was canceled."
                : "GitLab request failed (timeout).";
        }

        return "GitLab request failed.";
    }

    private static string RequestErrorCode(Exception ex, CancellationToken cancellationToken) {
        if (ex is TaskCanceledException) {
            return cancellationToken.IsCancellationRequested ? "canceled" : "timeout";
        }

        return ex.GetType().Name;
    }
}
