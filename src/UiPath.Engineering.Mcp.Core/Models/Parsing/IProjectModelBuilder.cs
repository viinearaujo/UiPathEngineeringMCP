using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Core.Parsing;

public interface IProjectModelBuilder {
    Task<UiPathProjectModel> BuildAsync(string projectPath, CancellationToken cancellationToken = default);
}
