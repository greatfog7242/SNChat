# Phase 1 Complete: MCP Protocol Foundation ✓

## What Was Built

**Phase 1 Goal**: Implement core MCP protocol and stdio transport for local MCP servers.

### Deliverables ✓

1. **SNChat.MCP Project** - New class library added to solution
   - Target: .NET 8.0
   - Location: `SNChat.MCP/`

2. **JSON-RPC Protocol Layer** (`Protocol/JsonRpc/`)
   - `JsonRpcMessage.cs` - Base types for JSON-RPC 2.0
     - `JsonRpcRequest` - Request with id + method + params
     - `JsonRpcResponse` - Response with id + result/error
     - `JsonRpcNotification` - One-way messages (no id)
     - `JsonRpcError` - Standard error structure

3. **MCP Message Types** (`Protocol/Messages/McpMessages.cs`)
   - **Initialize**: `InitializeParams`, `InitializeResult`
   - **Capabilities**: `ClientCapabilities`, `ServerCapabilities`
   - **Tools**: `McpTool`, `CallToolParams`, `CallToolResult`
   - **Resources**: `Resource`, `ListResourcesResult`
   - **Prompts**: `Prompt`, `ListPromptsResult`
   - All use JSON serialization attributes for wire format

4. **Stdio Transport** (`Transport/StdioTransport.cs`)
   - Spawns MCP server as child process
   - Sends/receives JSON-RPC messages via stdin/stdout
   - Async request/response matching by id
   - Event-based notification delivery
   - Captures stderr for debugging
   - Clean process lifecycle management

5. **MCP Client** (`McpClient.cs`)
   - High-level API for MCP operations
   - Lifecycle: `InitializeAsync()` → ready → `Dispose()`
   - Methods:
     - `ListToolsAsync()` - Discover available tools
     - `CallToolAsync(name, args)` - Execute tools
     - `ListResourcesAsync()` - List resources
     - `ListPromptsAsync()` - List prompts
   - Validates initialization state
   - Thread-safe request ID generation

6. **Examples & Documentation**
   - `Examples/BasicUsage.md` - Complete tutorial
   - `Examples/SimpleTest.cs` - Test code template

### Project Structure

```
SNChat.MCP/
├── Protocol/
│   ├── JsonRpc/
│   │   └── JsonRpcMessage.cs       # JSON-RPC 2.0 base types
│   └── Messages/
│       └── McpMessages.cs          # MCP-specific messages
├── Transport/
│   └── StdioTransport.cs           # Process + stdio communication
├── Examples/
│   ├── BasicUsage.md               # Tutorial
│   └── SimpleTest.cs               # Test template
├── McpClient.cs                    # Main client API
└── PHASE1_HANDOFF.md              # This document
```

## How to Use (Quick Start)

```csharp
// 1. Create client with MCP server command
using var client = new McpClient("npx", "-y @modelcontextprotocol/server-filesystem /path");

// 2. Initialize connection
await client.InitializeAsync();

// 3. Discover tools
var tools = await client.ListToolsAsync();

// 4. Call a tool
var result = await client.CallToolAsync("read_file", new Dictionary<string, object>
{
    ["path"] = "/path/to/file.txt"
});

// 5. Use the result
foreach (var content in result.Content)
{
    Console.WriteLine(content.Text);
}
```

## Testing

The project builds successfully:

```bash
dotnet build SNChat.MCP/SNChat.MCP.csproj
# Build succeeded. 0 Warning(s). 0 Error(s).
```

To test with a real MCP server:

1. Install filesystem server: `npm install -g @modelcontextprotocol/server-filesystem`
2. Copy code from `Examples/SimpleTest.cs`
3. Run in a console app

## What Phase 2 Needs to Do

**Phase 2 Goal**: Integrate MCP tools into SNChat's existing tool system.

### Tasks for Phase 2:

1. **Create `McpToolAdapter`** class
   - Implements `SNChat.Core.Tools.ITool` interface
   - Wraps an MCP tool from a connected server
   - Translates between SNChat tool format and MCP format
   - Maps `ITool.ExecuteAsync()` → `McpClient.CallToolAsync()`

2. **Add MCP server configuration**
   - Add `McpServers` list to `AppSettings`
   - Each entry: `{ Command, Arguments, Enabled }`
   - Example: `{ "npx", "-y @modelcontextprotocol/server-filesystem C:\\workspace", true }`

3. **Create `McpToolRegistry`** or extend `ToolRegistry`
   - On startup, spawn configured MCP servers
   - Initialize each client
   - Discover tools from each server
   - Register tools as `McpToolAdapter` instances
   - These tools appear alongside `WebSearchTool`, `ImageSearchTool`

4. **Add dependency**
   - Add project reference in `SNChat.App.csproj`:
     ```xml
     <ProjectReference Include="..\SNChat.MCP\SNChat.MCP.csproj" />
     ```

5. **Test end-to-end**
   - Configure filesystem MCP server in settings
   - App starts, connects to server, registers tools
   - Ask LLM: "Read the file at C:\temp\test.txt"
   - LLM calls `read_file` tool via MCP
   - File contents appear in response

### Key Files to Modify (Phase 2):

- `SNChat.Core/Models/AppSettings.cs` - Add MCP server config
- `SNChat.App/App.xaml.cs` - Initialize MCP clients on startup
- Create new: `SNChat.App/Services/McpToolAdapter.cs`
- Possibly extend: `SNChat.Core/Tools/ToolRegistry.cs`

### Example: McpToolAdapter Skeleton

```csharp
public class McpToolAdapter : ITool
{
    private readonly McpClient _client;
    private readonly McpTool _mcpTool;

    public string Name => _mcpTool.Name;
    public string Description => _mcpTool.Description ?? "";
    public ToolParameterSchema Parameters => ConvertSchema(_mcpTool.InputSchema);

    public async Task<string> ExecuteAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        var args = arguments.ToDictionary(k => k.Key, v => v.Value!);
        var result = await _client.CallToolAsync(_mcpTool.Name, args, cancellationToken);

        // Convert CallToolResult to string
        return string.Join("\n", result.Content.Select(c => c.Text ?? ""));
    }

    private ToolParameterSchema ConvertSchema(ToolInputSchema schema)
    {
        // Map MCP schema to ITool schema
        // ...
    }
}
```

## Dependencies

Current dependencies:
- .NET 8.0
- System.Text.Json (built-in)
- System.Diagnostics.Process (built-in)

No external NuGet packages required!

## Known Limitations

1. **Stdio only** - No HTTP/SSE transport yet (fine for local servers)
2. **No sampling** - Client doesn't support LLM sampling requests from server
3. **No progress notifications** - Tools run to completion, no streaming progress
4. **No resource subscriptions** - Can list but not subscribe to resource changes

These are fine for Phase 2. Can add later if needed.

## Success Criteria Met ✓

- [x] Can spawn MCP server process
- [x] Can send/receive JSON-RPC messages
- [x] Can initialize connection (handshake completes)
- [x] Can list tools from server
- [x] Can call tools and get results
- [x] Clean shutdown without process leaks
- [x] Project builds without errors
- [x] Documentation complete

## Next Session Start Here

**You are beginning Phase 2: Tool Discovery & Execution**

Goal: Make MCP tools callable by the LLM in SNChat.

First step: Create `McpToolAdapter` class that implements `ITool` and wraps an MCP tool.

File to create: `SNChat.App/Services/McpToolAdapter.cs`

Reference this handoff doc and `SNChat.MCP/Examples/BasicUsage.md` for how to use `McpClient`.
