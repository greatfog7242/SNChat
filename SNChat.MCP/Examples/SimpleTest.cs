// Quick test to verify MCP client works
// Uncomment and run in a console app to test

/*
using SNChat.MCP;

Console.WriteLine("SNChat MCP Client Test\n");

// Replace with your MCP server command
// Example: "npx" with args "-y @modelcontextprotocol/server-filesystem C:\\temp"
var serverCommand = "npx";
var serverArgs = "-y @modelcontextprotocol/server-filesystem .";

try
{
    using var client = new McpClient(serverCommand, serverArgs);

    client.ErrorReceived += (s, error) =>
    {
        Console.WriteLine($"[ERROR] {error}");
    };

    Console.WriteLine("Initializing connection...");
    await client.InitializeAsync();

    Console.WriteLine($"✓ Connected to {client.ServerInfo.Name} v{client.ServerInfo.Version}");
    Console.WriteLine();

    // List tools
    Console.WriteLine("Listing available tools...");
    var tools = await client.ListToolsAsync();

    Console.WriteLine($"✓ Found {tools.Count} tools:");
    foreach (var tool in tools)
    {
        Console.WriteLine($"  • {tool.Name}");
        if (tool.Description != null)
            Console.WriteLine($"    {tool.Description}");
    }
    Console.WriteLine();

    // Try calling a simple tool (list_directory)
    if (tools.Any(t => t.Name == "list_directory"))
    {
        Console.WriteLine("Testing list_directory tool...");
        var result = await client.CallToolAsync("list_directory", new Dictionary<string, object>
        {
            ["path"] = "."
        });

        Console.WriteLine($"✓ Tool executed successfully");
        if (result.IsError == true)
        {
            Console.WriteLine("  ⚠ Tool returned an error");
        }
        else
        {
            Console.WriteLine($"  Found {result.Content.Count} content items");
            foreach (var content in result.Content.Take(3))
            {
                if (content.Text != null)
                {
                    var preview = content.Text.Length > 100
                        ? content.Text.Substring(0, 100) + "..."
                        : content.Text;
                    Console.WriteLine($"  {preview}");
                }
            }
        }
    }

    Console.WriteLine("\n✓ All tests passed!");
}
catch (Exception ex)
{
    Console.WriteLine($"\n✗ Test failed: {ex.Message}");
    Console.WriteLine(ex.StackTrace);
}
*/
