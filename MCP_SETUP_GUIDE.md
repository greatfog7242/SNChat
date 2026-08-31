# MCP Setup Guide for SNChat

A complete beginner's guide to giving your AI assistant file system access (and more!) using MCP.

## What is MCP?

**MCP (Model Context Protocol)** lets your AI assistant use external tools like:
- 📁 Read/write files on your computer
- 🔧 Run git commands
- 🗄️ Query databases
- 🌐 Access web APIs
- ...and much more!

Think of it like giving your AI assistant superpowers to actually DO things on your computer, not just talk about them.

---

## Prerequisites

### 1. Install Node.js

MCP servers run on Node.js. Download and install it:

**Windows/Mac**: [nodejs.org](https://nodejs.org) - Download the LTS version

**Check installation:**
```bash
node --version
# Should show: v20.x.x or similar
```

### 2. Make Sure SNChat is Built

If you haven't already:
```bash
cd D:\Projects\c#\SNChat
dotnet build
```

---

## Quick Start: File System Access

Let's give your AI the ability to read and write files!

### Step 1: Install the Filesystem MCP Server

Open a terminal (PowerShell or Command Prompt) and run:

```bash
npm install -g @modelcontextprotocol/server-filesystem
```

This installs the official filesystem server globally on your computer.

### Step 2: Find Your Settings File

SNChat stores settings here:
```
Windows: C:\Users\YourName\AppData\Roaming\SNChat\config\settings.json
```

Quick way to find it:
1. Press `Win + R`
2. Type: `%APPDATA%\SNChat\config`
3. Press Enter
4. You'll see `settings.json`

**Note the `config` subfolder** — the file is not in the `SNChat` folder directly.

### Step 3: Edit Settings

Open `settings.json` in Notepad (or any text editor).

Find the `"Tools"` section and add `"McpServers"`:

```json
{
  "Providers": {
    ...existing settings...
  },
  "Defaults": {
    ...existing settings...
  },
  "Tools": {
    "ImageSource": "Auto",
    "WebSource": "Auto",
    "SafeSearch": true,
    "McpServers": [
      {
        "Name": "My Files",
        "Command": "npx",
        "Arguments": "-y @modelcontextprotocol/server-filesystem C:\\Users\\YourName\\Documents",
        "Enabled": true
      }
    ]
  }
}
```

**Keep the existing keys as they are** — SNChat writes this file in PascalCase
(`Tools`, `McpServers`, `Name`). Matching that style keeps the file consistent
with what the app writes back when you change a setting in the UI.

**Important**: Replace `C:\\Users\\YourName\\Documents` with the folder you want the AI to access.

**Note**: Use double backslashes `\\` in the path!

### Step 4: Save and Launch SNChat

1. Save `settings.json`
2. Close SNChat if it's running
3. Launch SNChat

You should see in the logs (`%APPDATA%\SNChat\logs\`):
```
Initializing 1 MCP server(s)
Connected to @modelcontextprotocol/server-filesystem
Discovered 8 tool(s)
Successfully registered 8/8 tools from My Files
```

### Step 5: Test It!

Create a test file first:
```bash
echo "Hello from MCP!" > C:\Users\YourName\Documents\test.txt
```

Then in SNChat, enable web search (the 🔎 checkbox) and ask:

**Try these:**
- "What files are in my Documents folder?"
- "Read the file test.txt from my Documents"
- "Create a file called ai-message.txt with the text 'AI can write files!'"
- "What's in the file ai-message.txt?"

The AI will use the MCP tools to actually read/write files!

---

## What Just Happened?

1. **SNChat started** and read `settings.json`
2. **Found your MCP server config** (the filesystem server)
3. **Spawned the server** as a background process: `npx @modelcontextprotocol/server-filesystem ...`
4. **Connected to it** and asked "what tools do you have?"
5. **Registered the tools** (read_file, write_file, etc.) so the AI can use them
6. **When you ask the AI** to read a file, it uses the `read_file` tool automatically!

---

## Safety First! 🛡️

### Choose Your Folder Carefully

The AI can **read, write, and delete** files in the folder you configure. Pick a safe folder:

✅ **Good choices:**
- `C:\\workspace\\ai-playground` (create a new folder just for AI)
- `C:\\Users\\YourName\\Documents\\AI-Files`
- `C:\\temp` (temporary files only)

❌ **Bad choices:**
- `C:\\` (entire C drive!)
- `C:\\Windows` (system files)
- Your entire Documents folder if it has sensitive files

### Multiple Folders

Want to give AI access to multiple folders? Configure multiple servers:

```json
"McpServers": [
  {
    "Name": "Work Docs",
    "Command": "npx",
    "Arguments": "-y @modelcontextprotocol/server-filesystem C:\\workspace\\docs",
    "Enabled": true
  },
  {
    "Name": "Scripts",
    "Command": "npx",
    "Arguments": "-y @modelcontextprotocol/server-filesystem C:\\scripts",
    "Enabled": true
  }
]
```

### Disable a Server

Set `"Enabled": false` to turn off a server without deleting the config:

```json
{
  "Name": "My Files",
  "Command": "npx",
  "Arguments": "-y @modelcontextprotocol/server-filesystem C:\\workspace",
  "Enabled": false
}
```

---

## Available Tools

Once the filesystem server is running, the AI can use these tools:

| Tool | What It Does | Example Ask |
|------|--------------|-------------|
| **read_file** | Read entire file | "Show me config.json" |
| **write_file** | Create/overwrite file | "Write 'hello' to test.txt" |
| **edit_file** | Edit specific lines | "In config.json, change port to 8080" |
| **list_directory** | List files/folders | "What's in my workspace folder?" |
| **create_directory** | Make new folder | "Create a folder called 'output'" |
| **move_file** | Rename/move file | "Rename old.txt to new.txt" |
| **search_files** | Find files by pattern | "Find all .py files" |
| **get_file_info** | File metadata | "How big is data.csv?" |

---

## More MCP Servers

### Git Repository Access

Give the AI git superpowers:

**Install:**
```bash
npm install -g @modelcontextprotocol/server-git
```

**Configure:**
```json
{
  "Name": "My Project Git",
  "Command": "npx",
  "Arguments": "-y @modelcontextprotocol/server-git --repository C:\\projects\\my-repo",
  "Enabled": true
}
```

**Ask AI:**
- "What's the latest commit?"
- "Show me uncommitted changes"
- "What branch am I on?"
- "Show the git log"

### SQLite Database

Query databases with AI:

**Install:**
```bash
npm install -g @modelcontextprotocol/server-sqlite
```

**Configure:**
```json
{
  "Name": "App Database",
  "Command": "npx",
  "Arguments": "-y @modelcontextprotocol/server-sqlite --db-path C:\\data\\app.db",
  "Enabled": true
}
```

**Ask AI:**
- "What tables are in the database?"
- "Show me all users where age > 25"
- "Count how many orders were placed today"

### Find More Servers

Browse community MCP servers:
- [Official MCP Servers](https://github.com/modelcontextprotocol)
- Search GitHub for "mcp server"

---

## Troubleshooting

### "No MCP servers configured"

**Problem**: The `mcpServers` array is missing or empty.

**Solution**: Add it to `settings.json` under the `"Tools"` section. Double-check
you edited `%APPDATA%\SNChat\config\settings.json` — the `config` subfolder, not
the `SNChat` folder itself.

### Server won't start

**Problem**: Can't spawn the MCP server process.

**Check:**
1. Is Node.js installed? Run `node --version`
2. Is the MCP server installed? Run `npm list -g @modelcontextprotocol/server-filesystem`
3. Is the command correct in settings.json?

**Logs**: Check `%APPDATA%\SNChat\logs\` for detailed errors.

### Tools not appearing

**Problem**: Server starts but tools don't work.

**Check:**
1. Is `"enabled": true` in the server config?
2. Is web search enabled (🔎 checkbox in SNChat)?
3. Check logs show "Successfully registered X tools"

**Solution**: Restart SNChat after changing settings.

### AI doesn't use the tools

**Problem**: AI responds with "I can't access files" even though tools are available.

**Check:**
1. Web search must be enabled (🔎 checkbox)
2. Ask clearly: "Read the file path.txt" not "What's in path.txt?"
3. Use the correct folder path you configured

### Permission denied

**Problem**: AI gets "permission denied" errors.

**Cause**: The folder isn't accessible or file is locked.

**Solution**:
- Make sure the folder exists
- Check Windows permissions on the folder
- Close any programs using the file

---

## Full Example Configuration

Here's a complete `settings.json` with multiple MCP servers:

```json
{
  "Providers": {
    "FreeTokenApiKey": "",
    "OpenRouterApiKey": "",
    "AnthropicApiKey": "",
    "OpenAIApiKey": "",
    "GoogleApiKey": "",
    "GoogleSearchEngineId": ""
  },
  "Defaults": {
    "Temperature": 0.7,
    "MaxTokens": 2048,
    "TopP": 0.9,
    "DefaultProvider": "Ollama",
    "DefaultModel": ""
  },
  "Tools": {
    "ImageSource": "Auto",
    "WebSource": "Auto",
    "SafeSearch": true,
    "McpServers": [
      {
        "Name": "Workspace Files",
        "Command": "npx",
        "Arguments": "-y @modelcontextprotocol/server-filesystem C:\\workspace",
        "Enabled": true
      },
      {
        "Name": "Project Git",
        "Command": "npx",
        "Arguments": "-y @modelcontextprotocol/server-git --repository C:\\workspace\\myproject",
        "Enabled": true
      },
      {
        "Name": "Database",
        "Command": "npx",
        "Arguments": "-y @modelcontextprotocol/server-sqlite --db-path C:\\data\\app.db",
        "Enabled": false
      }
    ]
  },
  "UI": {
    "Theme": "Light",
    "FontSize": 14,
    "ShowTimestamps": true,
    "EnableMarkdown": true,
    "SidebarWidth": 300
  },
  "Storage": {
    "ConversationsPath": "",
    "AutoSave": true,
    "MaxConversationsToKeep": 1000
  }
}
```

---

## Tips & Best Practices

### 1. Start Small
Begin with one folder access, test it, then add more servers.

### 2. Create an AI Playground
Make a dedicated folder just for AI experiments:
```bash
mkdir C:\ai-playground
```

### 3. Use Descriptive Names
Name your servers clearly:
```json
"name": "Work Documents"  // ✓ Good
"name": "Server1"         // ✗ Confusing
```

### 4. Keep Paths Short
Avoid deep nested paths if possible:
```
C:\workspace               // ✓ Simple
C:\Users\X\Documents\...\Y // ✗ Complex
```

### 5. Check Logs
When things don't work, logs are your friend:
```
%APPDATA%\SNChat\logs\snchat-YYYY-MM-DD.log
```

---

## Next Steps

You now have MCP working! Here's what to try:

1. **Experiment** - Ask the AI to create/read files
2. **Add git** - Give it repository access
3. **Try databases** - Connect to SQLite
4. **Build workflows** - "Read data.csv and summarize it"
5. **Explore servers** - Check out community MCP servers

---

## Getting Help

- **Documentation**: See `SNChat.MCP/PHASE2_TESTING.md` for advanced usage
- **Logs**: Check `%APPDATA%\SNChat\logs\` for errors  
- **MCP Docs**: [modelcontextprotocol.io](https://modelcontextprotocol.io)

---

## Summary

✅ **Installed** Node.js and MCP server  
✅ **Configured** `settings.json` with server details  
✅ **Tested** file operations with the AI  
✅ **Secured** by choosing safe folders  

Your AI assistant can now interact with your file system! 🎉
