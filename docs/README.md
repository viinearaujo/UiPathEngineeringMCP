# Operator docs

Docs for running and driving the UiPath Engineering MCP. Tool names live in `CopilotConnectorTools` and the [README](../README.md) toolkit.

| Doc | Use |
|-----|-----|
| [agent-connection.md](agent-connection.md) | HTTP, stdio, Dev Tunnel, auth, and the authoring loop |
| [copilot-prompts.md](copilot-prompts.md) | Copy-paste prompts for new work, changes, debug, and project docs |
| [copilot-studio-agent-instructions.txt](copilot-studio-agent-instructions.txt) | Paste into Copilot Studio. Source of truth for the Copilot loop |
| [copilot-studio-skills/](copilot-studio-skills/) | Thin Studio skill packages to upload (`rpa-authoring`, `guided-implementation-loop`, `project-docs`) |
| [copilot-idioms/](copilot-idioms/) | Coded workflow, coded test, and thin REFramework samples |
| [tool-api-style-guide.md](tool-api-style-guide.md) | Parameter names and error conventions for `[McpServerTool]` methods |

The server playbooks are `uipath-rpa` and `guided-implementation-loop` under `.agents/skills`. Do not upload that tree to Copilot Studio.
