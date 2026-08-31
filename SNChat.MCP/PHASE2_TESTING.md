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

Edit `%APPDATA%\SNChat\config\settings.json` and add MCP server configuration:

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
Connected to secure-filesystem-server v0.2.0
Discovered 14 tool(s) from Filesystem
Successfully registered 14/14 tools from Filesystem
MCP initialization complete. 1 server(s) connected, 14 tool(s) registered
```

The server reports itself as `secure-filesystem-server`; that is the internal
name of the `@modelcontextprotocol/server-filesystem` package, not a mismatch.

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

Verified against `secure-filesystem-server` v0.2.0 — 14 tools:

| Tool | Description |
|------|-------------|
| `read_text_file` | Read a file as text |
| `read_media_file` | Read an image or audio file as base64 |
| `read_multiple_files` | Read multiple files at once |
| `read_file` | Deprecated; use `read_text_file` |
| `write_file` | Create or overwrite a file |
| `edit_file` | Make line-based edits |
| `create_directory` | Create a new directory |
| `list_directory` | List directory contents with details |
| `list_directory_with_sizes` | Same, including file sizes |
| `directory_tree` | Recursive tree as JSON |
| `move_file` | Move or rename files |
| `search_files` | Recursive glob search |
| `get_file_info` | File metadata |
| `list_allowed_directories` | Report which folders are in scope |

## Other MCP Servers to Try

### Git Operations

Note the runner: git is a PyPI package run with `uvx`, not an npm package.
There is no `@modelcontextprotocol/server-git` on npm.

```json
{
  "Name": "Git",
  "Command": "uvx",
  "Arguments": "mcp-server-git --repository C:\\projects\\myrepo",
  "Enabled": true
}
```

Ask: "What's the most recent commit?" or "Show me uncommitted changes"

### SQLite Database

Also PyPI, also `uvx`.

```json
{
  "Name": "Database",
  "Command": "uvx",
  "Arguments": "mcp-server-sqlite --db-path C:\\data\\app.db",
  "Enabled": true
}
```

Ask: "What tables are in the database?" or "Show me users where age > 25"

### Multiple Servers

You can enable multiple MCP servers at once:

```json
{
  "Tools": {
    "McpServers": [
      {
        "Name": "Workspace Files",
        "Command": "npx",
        "Arguments": "-y @modelcontextprotocol/server-filesystem C:\\workspace",
        "Enabled": true
      },
      {
        "Name": "Project Git",
        "Command": "uvx",
        "Arguments": "mcp-server-git --repository C:\\workspace\\project",
        "Enabled": true
      },
      {
        "Name": "App Database",
        "Command": "uvx",
        "Arguments": "mcp-server-sqlite --db-path C:\\data\\app.db",
        "Enabled": false
      }
    ]
  }
}
```

All enabled servers start automatically, and all their tools are available to the LLM!

## Troubleshooting

### "No MCP servers configured"
Check `%APPDATA%\SNChat\config\settings.json` has the `mcpServers` array under `tools`.

### Server fails to start
- Check the command is correct: `npx` should be in PATH
- Check the MCP server package is installed: `npm install -g @modelcontextprotocol/server-filesystem`
- Check logs at `%APPDATA%\SNChat\logs\` for detailed error messages

### Tools not appearing
- Make sure `enabled: true` in the server config
- Check logs show "Successfully registered X tools"
- Try restarting the app

### LLM doesn't use MCP tools
- **Tick the 🔎 Web search checkbox.** Despite the label it gates *every* tool,
  MCP included; unchecked, no tools are sent to the model at all
- The model must support tool calling — many small local models ignore tool
  definitions entirely
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
