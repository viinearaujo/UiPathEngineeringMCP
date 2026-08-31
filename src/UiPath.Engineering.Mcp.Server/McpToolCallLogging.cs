using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace UiPath.Engineering.Mcp.Server;

internal static class McpToolCallLogging {
    public const string LoggerCategory = "UiPath.Engineering.Mcp.Server.ToolCalls";

    public static readonly McpRequestFilter<CallToolRequestParams, CallToolResult> Filter =
        next => async (context, cancellationToken) => {
            var logger = context.Services?.GetService<ILoggerFactory>()?.CreateLogger(LoggerCategory);
            var toolName = context.Params?.Name ?? "(unknown)";
            var sw = Stopwatch.StartNew();
            try {
                var result = await next(context, cancellationToken);
                sw.Stop();
                var (status, errorCode) = Describe(result);
                logger?.LogInformation(
                    "Tool {ToolName} duration {DurationMs}ms status {Status} errorCode {ErrorCode}",
                    toolName,
                    sw.ElapsedMilliseconds,
                    status,
                    errorCode);
                return result;
            } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
                sw.Stop();
                logger?.LogInformation(
                    "Tool {ToolName} duration {DurationMs}ms status {Status} errorCode {ErrorCode}",
                    toolName,
                    sw.ElapsedMilliseconds,
                    "canceled",
                    "canceled");
                throw;
            } catch (Exception ex) {
                sw.Stop();
                logger?.LogInformation(
                    "Tool {ToolName} duration {DurationMs}ms status {Status} errorCode {ErrorCode}",
                    toolName,
                    sw.ElapsedMilliseconds,
                    "error",
                    ex.GetType().Name);
                throw;
            }
        };

    internal static (string Status, string? ErrorCode) Describe(CallToolResult result) {
        if (result.StructuredContent is { } element
            && TryDescribeElement(element, out var status, out var errorCode)) {
            return (status, errorCode);
        }

        return (result.IsError is true ? "error" : "success", null);
    }

    private static bool TryDescribeElement(JsonElement element, out string status, out string? errorCode) {
        status = "success";
        errorCode = null;
        if (element.ValueKind != JsonValueKind.Object) {
            return false;
        }

        foreach (var property in element.EnumerateObject()) {
            if (property.NameEquals("status") || property.NameEquals("Status")) {
                if (property.Value.ValueKind == JsonValueKind.String) {
                    status = property.Value.GetString() ?? status;
                }
            } else if (property.NameEquals("errorCode") || property.NameEquals("ErrorCode")) {
                if (property.Value.ValueKind == JsonValueKind.String) {
                    errorCode = property.Value.GetString();
                }
            } else if ((property.NameEquals("errorDetails") || property.NameEquals("ErrorDetails"))
                       && property.Value.ValueKind == JsonValueKind.Array
                       && errorCode is null) {
                foreach (var item in property.Value.EnumerateArray()) {
                    if (item.ValueKind != JsonValueKind.Object) {
                        continue;
                    }

                    foreach (var nested in item.EnumerateObject()) {
                        if ((nested.NameEquals("errorCode") || nested.NameEquals("ErrorCode"))
                            && nested.Value.ValueKind == JsonValueKind.String) {
                            errorCode = nested.Value.GetString();
                            break;
                        }
                    }

                    break;
                }
            }
        }

        return true;
    }
}
