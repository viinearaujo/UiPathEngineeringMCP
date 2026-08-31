using Microsoft.Extensions.Options;
using UiPath.Engineering.Mcp.Core.Configuration;

namespace UiPath.Engineering.Mcp.Server;

public sealed class McpHttpAuthOptionsValidator : IValidateOptions<McpServerOptions> {
    private readonly IHostEnvironment _environment;

    public McpHttpAuthOptionsValidator(IHostEnvironment environment) {
        _environment = environment;
    }

    public ValidateOptionsResult Validate(string? name, McpServerOptions options) {
        ArgumentNullException.ThrowIfNull(options);
        var auth = options.HttpAuth ?? new HttpAuthOptions();
        var error = HttpAuthEvaluator.ValidateHttpStartup(auth, _environment.EnvironmentName);
        return error is null
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(error);
    }
}
