# MCP Client Basic Usage Example

This example demonstrates how to connect to an MCP server and use its tools.

## Prerequisites

You need an MCP server installed. For testing with the filesystem server:

```bash
# Install the official MCP filesystem server (Node.js required)
npm install -g @modelcontextprotocol/server-filesystem
```

## Example Code

```csharp
using SNChat.MCP;

// Create client connecting to filesystem MCP server
// Server allows access to the specified directory
using var client = new McpClient("npx", "-y @modelcontextprotocol/server-filesystem C:\\temp");

// Subscribe to errors
client.ErrorReceived += (s, error) => Console.WriteLine($"Error: {error}");

// Initialize the connection
await client.InitializeAsync();
Console.WriteLine($"Connected to: {client.ServerInfo.Name} v{client.ServerInfo.Version}");

// List available tools
var tools = await client.ListToolsAsync();
Console.WriteLine($"\nAvailable tools ({tools.Count}):");
foreach (var tool in tools)
{
    Console.WriteLine($"  - {tool.Name}: {tool.Description}");
}

// Call a tool - read a directory
var result = await client.CallToolAsync("read_file", new Dictionary<string, object>
{
    ["path"] = "C:\\temp\\test.txt"
});

// Print result
foreach (var content in result.Content)
{
    if (content.Text != null)
        Console.WriteLine($"\nFile contents:\n{content.Text}");
}
```

## Expected Output

```
Connected to: @modelcontextprotocol/server-filesystem v0.1.0

Available tools (5):
  - read_file: Read the complete contents of a file
  - read_multiple_files: Read multiple files simultaneously
  - write_file: Create new file or overwrite existing
  - edit_file: Make line-based edits to a text file
  - list_directory: Get detailed listing of directory contents

File contents:
Hello from MCP!
```

## What's Happening

1. **Create Client**: Spawns the MCP server as a child process
2. **Initialize**: Performs the MCP handshake (initialize → initialized)
3. **List Tools**: Discovers what the server can do
4. **Call Tool**: Executes a tool and gets the result
5. **Dispose**: Shuts down the server process cleanly

## Server Capabilities

After initialization, you can check what the server supports:

```csharp
if (client.ServerCapabilities.Tools != null)
    Console.WriteLine("Server supports tools");

if (client.ServerCapabilities.Resources != null)
    Console.WriteLine("Server supports resources");

if (client.ServerCapabilities.Prompts != null)
    Console.WriteLine("Server supports prompts");
```

## Error Handling

```csharp
try
{
    var result = await client.CallToolAsync("read_file", new Dictionary<string, object>
    {
        ["path"] = "C:\\nonexistent.txt"
    });

    if (result.IsError == true)
    {
        Console.WriteLine("Tool returned an error");
    }
}
catch (McpException ex)
{
    Console.WriteLine($"MCP error: {ex.Message}");
}
```

## Next Steps

- See Phase 2 documentation for integrating MCP tools into SNChat's ITool system
- Check Protocol/Messages/ for all available MCP message types
- Review Transport/StdioTransport.cs for low-level details
