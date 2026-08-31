# SNChat.MCP

Model Context Protocol (MCP) client library for SNChat.

## What is MCP?

MCP is Anthropic's standardized protocol for connecting AI assistants to external tools, data sources, and services. It allows LLMs to:
- Access local filesystems
- Query databases  
- Execute git operations
- Connect to APIs
- And much more via community-built servers

## Project Status

✅ **Phase 1 Complete** - Core protocol implementation
- JSON-RPC 2.0 messaging
- Stdio transport for local servers
- Full MCP lifecycle (initialize → ready → shutdown)
- Tool discovery and execution
- Resource and prompt support

🚧 **Phase 2 Next** - Integration with SNChat
- Wrap MCP tools as `ITool` instances
- Register in SNChat's tool system
- LLM can call MCP tools like web_search

## Quick Start

```csharp
using SNChat.MCP;

// Connect to MCP server
using var client = new McpClient("npx", "-y @modelcontextprotocol/server-filesystem /path");

// Initialize
await client.InitializeAsync();

// List available tools
var tools = await client.ListToolsAsync();

// Call a tool
var result = await client.CallToolAsync("read_file", new Dictionary<string, object>
{
    ["path"] = "/path/to/file.txt"
});
```

## Documentation

- **[Phase 1 Handoff](PHASE1_HANDOFF.md)** - What was built, how it works, what's next
- **[Basic Usage](Examples/BasicUsage.md)** - Complete tutorial with examples
- **[Simple Test](Examples/SimpleTest.cs)** - Quick test code

## Architecture

```
McpClient
    ↓ (uses)
StdioTransport  
    ↓ (spawns)
MCP Server Process (Node.js, Python, etc.)
    ↓ (stdio)
JSON-RPC Messages
```

## Available MCP Servers

Community MCP servers you can use. Note that the runner differs by registry —
npm packages run with `npx`, PyPI packages with `uvx`:

| Server | Package | Runner |
|---|---|---|
| File operations | `@modelcontextprotocol/server-filesystem` | `npx` |
| Persistent memory | `@modelcontextprotocol/server-memory` | `npx` |
| Git operations | `mcp-server-git` | `uvx` |
| SQLite queries | `mcp-server-sqlite` | `uvx` |
| HTTP fetch | `mcp-server-fetch` | `uvx` |

Many more at https://github.com/modelcontextprotocol/servers

## License

Part of SNChat project.
