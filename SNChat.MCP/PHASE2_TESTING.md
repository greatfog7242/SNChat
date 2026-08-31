# Phase 2 Testing Guide: MCP Tool Integration

Phase 2 is complete! MCP tools are now integrated into SNChat. This guide shows how to test it.

## What Was Built

✅ **McpToolAdapter** - Wraps MCP tools as ITool instances
✅ **McpServerConfig** - Settings for MCP servers
✅ **McpService** - Manages server lifecycle and tool registration  
✅ **App integration** - Auto-starts MCP servers on app launch

## Quick Test: Filesystem Tools

### 1. Install MCP Filesystem Server

```bash
npm install -g @modelcontextprotocol/server-filesystem
```

### 2. Configure SNChat

Edit `%APPDATA%\SNChat\settings.json` and add MCP server configuration:

```json
{
  "providers": { ... },
  "tools": {
    "imageSource": "Auto",
    "mcpServers": [
      {
        "name": "Filesystem",
        "command": "npx",
        "arguments": "-y @modelcontextprotocol/server-filesystem C:\\temp",
        "enabled": true
      }
    ]
  }
}
```

**Important**: Replace `C:\\temp` with a directory path you want the LLM to access.

### 3. Launch SNChat

The app will:
1. Load settings
2. Find the filesystem MCP server config
3. Spawn `npx -y @modelcontextprotocol/server-filesystem C:\temp`
4. Initialize the connection
5. Discover tools (read_file, write_file, list_directory, etc.)
6. Register them alongside web_search and image_search

Check the logs at `%APPDATA%\SNChat\logs\` for:
```
Initializing 1 MCP server(s)
Connecting to MCP server: Filesystem (npx -y @modelcontextprotocol/server-filesystem C:\temp)
Connected to @modelcontextprotocol/server-filesystem v0.1.0
Discovered 5 tool(s) from Filesystem
Successfully registered 5/5 tools from Filesystem
MCP initialization complete. 1 server(s) connected, 5 tool(s) registered
```

### 4. Test in Conversation

Create a test file first:
```bash
echo "Hello from MCP!" > C:\temp\test.txt
```

Then in SNChat, ask the LLM:
```
"Read the file at C:\temp\test.txt"
```

The LLM should:
1. Recognize it needs to read a file
2. See the `read_file` tool is available
3. Call `read_file` with `{"path": "C:\\temp\\test.txt"}`
4. Receive the contents: "Hello from MCP!"
5. Show you the file contents in its response

### 5. Try Other Tools

```
"List all files in C:\temp"
→ Uses list_directory tool

"Create a file at C:\temp\hello.txt with the content 'AI was here'"
→ Uses write_file tool

"What's in the file C:\temp\config.json?"
→ Uses read_file tool
```

## Available Filesystem Tools

When you configure the filesystem server, these tools become available:

| Tool | Description |
|------|-------------|
| `read_file` | Read complete contents of a file |
| `read_multiple_files` | Read multiple files at once |
| `write_file` | Create or overwrite a file |
| `edit_file` | Make line-based edits |
| `list_directory` | List directory contents with details |
| `create_directory` | Create a new directory |
| `move_file` | Move or rename files |
| `search_files` | Search for files by pattern |

## Other MCP Servers to Try

### Git Operations
```json
{
  "name": "Git",
  "command": "npx",
  "arguments": "-y @modelcontextprotocol/server-git --repository C:\\projects\\myrepo",
  "enabled": true
}
```

Ask: "What's the most recent commit?" or "Show me uncommitted changes"

### SQLite Database
```json
{
  "name": "Database",
  "command": "npx",
  "arguments": "-y @modelcontextprotocol/server-sqlite --db-path C:\\data\\app.db",
  "enabled": true
}
```

Ask: "What tables are in the database?" or "Show me users where age > 25"

### Multiple Servers

You can enable multiple MCP servers at once:

```json
{
  "tools": {
    "mcpServers": [
      {
        "name": "Workspace Files",
        "command": "npx",
        "arguments": "-y @modelcontextprotocol/server-filesystem C:\\workspace",
        "enabled": true
      },
      {
        "name": "Project Git",
        "command": "npx",
        "arguments": "-y @modelcontextprotocol/server-git --repository C:\\workspace\\project",
        "enabled": true
      },
      {
        "name": "App Database",
        "command": "npx",
        "arguments": "-y @modelcontextprotocol/server-sqlite --db-path C:\\data\\app.db",
        "enabled": false
      }
    ]
  }
}
```

All enabled servers start automatically, and all their tools are available to the LLM!

## Troubleshooting

### "No MCP servers configured"
Check `%APPDATA%\SNChat\settings.json` has the `mcpServers` array under `tools`.

### Server fails to start
- Check the command is correct: `npx` should be in PATH
- Check the MCP server package is installed: `npm install -g @modelcontextprotocol/server-filesystem`
- Check logs at `%APPDATA%\SNChat\logs\` for detailed error messages

### Tools not appearing
- Make sure `enabled: true` in the server config
- Check logs show "Successfully registered X tools"
- Try restarting the app

### LLM doesn't use MCP tools
- The LLM must have web search enabled (the tools checkbox)
- Make sure you're asking for something the tool can do
- The filesystem server only has access to the directory you configured

## Architecture

```
SNChat App Startup
    ↓
McpService.InitializeAsync()
    ↓
For each enabled MCP server:
    1. Spawn process (npx, python, etc.)
    2. McpClient.InitializeAsync()
    3. McpClient.ListToolsAsync()
    4. For each tool:
        → Create McpToolAdapter
        → Register in ToolRegistry
    ↓
Tools available to LLM alongside web_search, image_search
```

## Success Criteria ✓

- [x] MCP servers start automatically from settings
- [x] Tools discovered and registered
- [x] LLM can call MCP tools
- [x] Tool results flow back to conversation
- [x] Multiple servers can run simultaneously
- [x] Graceful shutdown when app closes

## What's Next (Optional Phase 3+)

Future enhancements:
- **Settings UI** - Configure MCP servers without editing JSON
- **Server status** - Show which servers are connected in UI
- **Tool browser** - See all available tools in settings
- **Resource support** - Access MCP resources, not just tools
- **Prompt templates** - Use MCP-provided prompts
- **Auto-discovery** - Detect MCP servers in common locations

## Phase 2 Complete!

MCP integration is fully functional. The LLM can now use external tools from any MCP server!

Read `PHASE1_HANDOFF.md` for protocol details and `SNChat.MCP/README.md` for project overview.
