using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UiPath.Engineering.Mcp.Providers.UiPathCli;

namespace UiPath.Engineering.Mcp.Server;

/// <summary>
/// Fire-and-forget <c>uip --version</c> on server start so the first Copilot CLI call avoids cold start.
/// </summary>
internal sealed class UiPathCliWarmupHostedService : IHostedService {
    private readonly IUiPathCliProvider _cli;
    private readonly ILogger<UiPathCliWarmupHostedService> _logger;

    public UiPathCliWarmupHostedService(IUiPathCliProvider cli, ILogger<UiPathCliWarmupHostedService> logger) {
        _cli = cli;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken) {
        _ = Task.Run(async () => {
            try {
                if (_cli is UiPathCliProvider concrete) {
                    await concrete.WarmupAsync(CancellationToken.None).ConfigureAwait(false);
                } else {
                    await _cli.RunAsync("version", "--version", cancellationToken: CancellationToken.None)
                        .ConfigureAwait(false);
                }
            } catch (Exception ex) {
                _logger.LogDebug(ex, "UiPath CLI warm-up failed (ignored).");
            }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
