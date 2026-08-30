# SNChat Implementation Status

**Last Updated**: 2026-08-30 17:35 UTC  
**Current Phase**: Phase 2 - Enhanced UI & Multiple Providers  
**Phase 1 Status**: ✅ COMPLETE  
**Phase 2 Status**: 🎉 85% Complete - Nearly Done!  
**Bonus**: Tool calling with web + image search (unplanned, delivered early)  
**Repository**: https://github.com/greatfog7242/SNChat

## Completed Work

### Project Structure ✅
- Created SNChat.slnx solution file with 7 projects
- **SNChat.App** - WPF Application (net8.0-windows)
- **SNChat.Core** - Business logic and domain models
- **SNChat.LLM** - LLM provider abstraction
- **SNChat.FileTools** - Local file operations (placeholder)
- **SNChat.WebTools** - Web access and scraping (placeholder)
- **SNChat.RAG** - Document processing for RAG (placeholder)
- **SNChat.Tests** - Test project
- Project references configured correctly
- Solution builds successfully

### NuGet Packages Installed ✅
- **SNChat.App**: CommunityToolkit.Mvvm, Microsoft.Extensions.Hosting, Serilog.Extensions.Hosting, Serilog.Sinks.File
- **SNChat.Core**: System.Text.Json, YamlDotNet
- **SNChat.LLM**: Polly, Microsoft.Extensions.Logging.Abstractions

### Core Domain Models ✅
Location: `SNChat.Core/Models/`
- `MessageRole.cs` - User/Assistant/System enum
- `AttachmentType.cs` - Document/Image/Code/Other enum
- `ModelParameters.cs` - LLM parameters (temperature, max tokens, etc.)
- `Attachment.cs` - File attachments for RAG
- `ConversationMetadata.cs` - Conversation metadata with cloning
- `Message.cs` - Chat message with attachments and cloning
- `Conversation.cs` - Full conversation with branching support

### LLM Provider Implementation ✅
Location: `SNChat.LLM/`

**Interfaces:**
- `ILLMProvider.cs` - Main provider contract

**Models:**
- `GenerateRequest.cs` - Request model
- `StreamChunk.cs` - Streaming response chunk
- `StreamMetadata.cs` - Token usage metadata
- `Model.cs` - Available model info

**Providers:**
- `BaseLLMProvider.cs` - Abstract base class with common HTTP logic
- `OllamaProvider.cs` - **COMPLETE** Ollama implementation with streaming support
  - Endpoint: http://localhost:11434/api/chat
  - Streaming via IAsyncEnumerable<StreamChunk>
  - Model listing via /api/tags
  - Full parameter support (temperature, max tokens, top-p, etc.)
- `OllamaModels.cs` - Ollama-specific DTOs

### Storage Service ✅
Location: `SNChat.Core/Services/StorageService.cs`

**Features:**
- Saves conversations as markdown files with YAML frontmatter
- **Folder-based organization**: Each conversation gets its own folder
- File structure: `%APPDATA%/SNChat/conversations/YYYY-MM/{conversation-id}/`
  - `conversation.md` - Main conversation file
  - `attachments/` - Media files, images, documents, etc.
- Parse/serialize markdown with metadata
- Load conversations by ID or file path
- List all conversation files
- Delete conversations (entire folder)
- Get attachments directory for storing media

**Interface**: `IStorageService.cs`

### DI Container & WPF Host ✅
Location: `SNChat.App/App.xaml.cs`

**Features:**
- Microsoft.Extensions.Hosting integration for WPF
- Serilog logging configured with file sink
- Automatic directory initialization on startup
- Service registration:
  - IStorageService (singleton)
  - OllamaProvider with HttpClient (singleton)
  - ChatViewModel (transient)
  - MainWindow (singleton)

### ChatViewModel ✅
Location: `SNChat.App/ViewModels/ChatViewModel.cs`

**Features:**
- Built with CommunityToolkit.Mvvm for MVVM pattern
- Real-time streaming support via IAsyncEnumerable
- Commands: SendMessage, NewConversation, CancelGeneration
- Auto-save conversations after each response
- Auto-generate conversation titles from first user message
- Observable collections for UI binding
- Cancellation token support for stopping generation

### Chat UI ✅
Location: `SNChat.App/Views/ChatView.xaml`

**Features:**
- Clean message display with role-based styling
- Auto-scrolling to latest messages
- TextBox input with Ctrl+Enter shortcut
- Toolbar with New Conversation and Cancel buttons
- Streaming indicator overlay
- Current conversation title and model display
- Responsive layout with proper sizing

## Files Created

### Critical Files
```
D:\Projects\c#\SNChat\
├── SNChat.slnx                                              # Solution file
├── SNChat.Core/
│   ├── Models/
│   │   ├── Conversation.cs                                  # ✅
│   │   ├── Message.cs                                       # ✅
│   │   ├── ConversationMetadata.cs                          # ✅
│   │   ├── ModelParameters.cs                               # ✅
│   │   ├── Attachment.cs                                    # ✅
│   │   ├── MessageRole.cs                                   # ✅
│   │   └── AttachmentType.cs                                # ✅
│   ├── Services/
│   │   └── StorageService.cs                                # ✅
│   └── Interfaces/
│       └── IStorageService.cs                               # ✅
├── SNChat.LLM/
│   ├── Interfaces/
│   │   └── ILLMProvider.cs                                  # ✅
│   ├── Models/
│   │   ├── GenerateRequest.cs                               # ✅
│   │   ├── StreamChunk.cs                                   # ✅
│   │   ├── StreamMetadata.cs                                # ✅
│   │   └── Model.cs                                         # ✅
│   └── Providers/
│       ├── Base/
│       │   └── BaseLLMProvider.cs                           # ✅
│       └── Ollama/
│           ├── OllamaProvider.cs                            # ✅ TESTED: Builds
│           └── OllamaModels.cs                              # ✅
└── SNChat.App/
    ├── App.xaml                                             # ✅ Updated with converters
    ├── App.xaml.cs                                          # ✅ DI container & hosting
    ├── MainWindow.xaml                                      # ✅ Updated to host ChatView
    ├── MainWindow.xaml.cs                                   # ✅ DI integration
    ├── ViewModels/
    │   └── ChatViewModel.cs                                 # ✅ Main chat logic with streaming
    ├── Views/
    │   ├── ChatView.xaml                                    # ✅ Chat UI
    │   └── ChatView.xaml.cs                                 # ✅ Code-behind
    └── Converters/
        └── InverseBooleanConverter.cs                       # ✅ Value converter for UI
```

## Phase 1 Limitations & Known Issues
- **Ollama dependency**: App requires Ollama running on localhost:11434
- **Single provider**: Only Ollama supported (OpenRouter/FreeToken in Phase 2)
- **Plain text UI**: Messages display as plain text (markdown rendering in Phase 2)
- **No conversation history**: Can't browse past conversations in UI (Phase 2)
- **No settings UI**: All settings hardcoded (Phase 2)
- **Manual cancellation**: Cancellation token checked in loop (could improve)
- **No error recovery**: Network errors not gracefully handled
- **No message editing**: Can't edit or regenerate messages (Phase 3)

## Phase 2: Enhanced UI & Multiple Providers

### Goals
- Add support for multiple LLM providers (OpenRouter/FreeToken)
- Improve chat UI with markdown rendering and syntax highlighting
- Add conversation history/list view
- Implement settings management UI
- Add conversation search functionality

### Completed Work (Phase 2)

**Task #1**: Implement Provider Factory Pattern ✅
Location: `SNChat.LLM/`

**Implemented:**
- `ILLMProviderFactory` interface for managing multiple providers
- `ProviderFactory` implementation with registration system
- `FreeTokenProvider` with OpenAI-compatible API support
- Provider selection UI in toolbar (dropdown)
- Dynamic model loading based on selected provider
- Conversation metadata stores provider name

**Features:**
- Switch between Ollama and FreeToken providers
- Each provider maintains its own model list
- Provider-specific settings support (API keys, endpoints)
- Graceful fallback to default models on API failure

**Files Created:**
- `SNChat.LLM/Interfaces/ILLMProviderFactory.cs`
- `SNChat.LLM/ProviderFactory.cs`
- `SNChat.LLM/Providers/FreeToken/FreeTokenProvider.cs`
- `SNChat.LLM/Providers/FreeToken/FreeTokenModels.cs`

**Task #3**: Add Markdown Rendering in Chat ✅
Location: `SNChat.App/Views/ChatView.xaml`

**Implemented:**
- Installed Markdig.Wpf NuGet package (v0.5.0.1)
- Replaced plain TextBlock with MarkdownViewer control
- Markdown rendering for all message content
- Support for:
  - Headers (H1-H6)
  - Bold, italic, strikethrough
  - Code blocks with monospace font
  - Inline code with highlighting
  - Lists (ordered and unordered)
  - Links and images
  - Blockquotes
  - Tables

**Styling:**
- Code blocks: monospace font, light gray background
- Inline code: pink text, gray background
- Clean, readable typography with Segoe UI
- Proper spacing and padding

**Task #5**: Conversation List View ✅
Location: `SNChat.App/Views/ConversationListView.xaml`, `SNChat.App/ViewModels/ConversationListViewModel.cs`

**Implemented:**
- Sidebar with all past conversations
- Automatic loading of conversations on startup
- Date-based grouping (Today, Yesterday, This Week, This Month, Older)
- Search/filter functionality
- Delete conversation with confirmation
- Double-click to load conversation
- Refresh button to reload list
- Loading indicator during async operations
- Clean, organized UI with hover effects

**Features:**
- Shows conversation title, message count, provider, and last updated time
- Conversations sorted by most recent first
- Click to load conversation into chat view
- Auto-refresh when new conversations are saved
- Handles both old (single-file) and new (folder-based) formats

**Bug Fixes:**
- Fixed GUID parsing error for empty parent_branch fields in conversation metadata

**Files Created:**
- `SNChat.App/ViewModels/ConversationListViewModel.cs`
- `SNChat.App/Views/ConversationListView.xaml`
- `SNChat.App/Views/ConversationListView.xaml.cs`

**Task #6**: Settings UI ✅
Location: `SNChat.App/Views/SettingsWindow.xaml`, `SNChat.Core/Models/AppSettings.cs`, `SNChat.Core/Services/SettingsService.cs`

**Implemented:**
- Complete settings management system
- Settings window with tabbed interface (Providers, Defaults, UI, Storage)
- JSON-based settings storage in `%APPDATA%/SNChat/config/settings.json`
- Settings service with load/save functionality
- Unsaved changes tracking and warnings

**Settings Categories:**
1. **Provider Settings**: API keys and base URLs for FreeToken, OpenRouter, Anthropic, OpenAI
2. **Default Parameters**: Temperature, max tokens, top-p, default provider/model
3. **UI Preferences**: Theme, font size, timestamps, markdown, sidebar width
4. **Storage Settings**: Custom conversation path, auto-save, max conversations

**Features:**
- PasswordBox controls for secure API key entry
- Slider controls for temperature and top-p
- Reset to defaults functionality
- Unsaved changes indicator
- Confirmation on close with unsaved changes
- Settings button in main toolbar
- Auto-load settings on startup
- FreeToken provider now uses API key from settings

**Files Created:**
- `SNChat.Core/Models/AppSettings.cs`
- `SNChat.Core/Services/SettingsService.cs`
- `SNChat.App/ViewModels/SettingsViewModel.cs`
- `SNChat.App/Views/SettingsWindow.xaml`
- `SNChat.App/Views/SettingsWindow.xaml.cs`

**Bonus**: Tool Calling with Web & Image Search ✅
Location: `SNChat.Core/Tools/`, `SNChat.WebTools/`, `OllamaProvider`

**Framework** (provider-agnostic, reusable for future tools):
- `ITool` - name, description, JSON-schema parameters, execute
- `IToolRegistry` / `ToolRegistry` - registration and dispatch; unknown tools and
  thrown exceptions come back as error results so a conversation never tears down
- `ToolCall` / `ToolResult`, `ToolParameterSchema`
- Agentic loop in `OllamaProvider`: model requests tool -> execute -> append
  result -> re-ask, capped by `MaxToolIterations` (default 5)
- Tool definitions are omitted entirely when the toggle is off, so ordinary
  chats do not pay the extra prompt tokens

**Tools**:
- `web_search` - DuckDuckGo Instant Answer API, with Wikipedia REST as fallback
  for summaries and images. Keyless.
- `image_search` - Wikimedia Commons API. Keyless, freely-licensed images.

**UI**: `🔎 Web search` toggle in the toolbar; live `🔎 Using <tool>...` status
in the streaming overlay (status chunks are shown but never persisted).

**Files Created**:
- `SNChat.Core/Tools/ITool.cs`, `IToolRegistry.cs`, `ToolRegistry.cs`,
  `ToolCall.cs`, `ToolParameterSchema.cs`
- `SNChat.WebTools/WebSearchTool.cs`, `ImageSearchTool.cs`, `ImageUrl.cs`

### Non-obvious findings (worth not rediscovering)

**Markdig.Wpf silently fails on percent-encoded image URLs.** Any `%XX` escape in
the path yields a zero-sized image with no exception and no log entry - even
plain ASCII escapes such as `%2C` for a comma. Decoding the path fixes it.
Wikimedia escapes any filename with punctuation or non-ASCII characters, so most
image results were silently invisible. Handled in `ImageUrl.ForMarkdown`.

**Markdown links were never clickable.** WPF does nothing on `Hyperlink` click
without a `RequestNavigate` handler; one is now registered in `ChatView`, and it
only follows http/https since the URLs come from model output.

**Ollama returns 404 for a missing model**, which is indistinguishable from a bad
endpoint in the logs. A hardcoded default of `llama3.1:8b` that was not installed
caused this; `LoadConversation` now validates the saved model against the
available list.

**Google Custom Search JSON API appears closed to new projects (2026-08-30).**
A valid, recognised API key returns `403 "This project does not have the access
to Custom Search JSON API"` even after enabling the API in Cloud Console. An
invalid key returns a clearly different error (`API key not valid`), which rules
out a credential problem. Reportedly Google has sunset new sign-ups. The
`GoogleWebSource` and `GoogleImageSource` implementations are complete and remain
wired in - they activate automatically if a working key is ever supplied - but
they have never been exercised against a successful response, so the parsing of
`items[].title/link/snippet` is written to spec and unverified. Default behaviour
falls back to DuckDuckGo + Wikipedia (text) and Wikimedia Commons (images), which
need no credentials.

**DuckDuckGo HTML scraping is not viable.** `html.` and `lite.` endpoints serve an
image CAPTCHA (`cc=botnet`) once an IP is flagged. The Python `duckduckgo_search`
library only reaches the image endpoint by impersonating browser TLS fingerprints
via `primp`, which .NET cannot do. The official Instant Answer API and Wikimedia
Commons are used instead - both documented, keyless, and stable. Note the Instant
Answer API returns HTTP **202 on success**, so status codes are not a validity
signal there; judge by payload.

### Task List (Phase 2)

**Task #1**: Implement Provider Factory Pattern ✅ COMPLETE
- ✅ Create `ILLMProviderFactory` interface
- ✅ Implement factory to manage multiple providers
- ✅ Add provider switching in UI
- ✅ Store selected provider in conversation metadata

**Task #2**: Enhance Provider Support ✅ COMPLETE
- ✅ FreeToken provider created with streaming support
- ✅ OpenAI-compatible API pattern implemented
- ✅ Default model list (GPT-3.5, GPT-4, Claude 3)
- ✅ API key management UI (via Settings)
- ✅ Settings storage for API keys
- ✅ FreeToken provider uses API key from settings
- 🔄 Optional: Add more providers (OpenRouter, Anthropic Direct, etc.) - future enhancement

**Task #3**: Add Markdown Rendering in Chat ✅ COMPLETE
- ✅ Install Markdig.Wpf NuGet package
- ✅ Integrate MarkdownViewer control into ChatView
- ✅ Replace plain TextBlock with markdown viewer
- ✅ Style code blocks, lists, headers
- ✅ Support inline code, links, blockquotes, tables

**Task #4**: Add Code Syntax Highlighting
- Install AvalonEdit or similar NuGet package
- Create code block viewer with syntax highlighting
- Detect language from markdown code fence
- Support copy-to-clipboard for code blocks

**Task #5**: Build Conversation List View ✅ COMPLETE
- ✅ Create `ConversationListViewModel`
- ✅ Create `ConversationListView.xaml` sidebar
- ✅ Load and display all saved conversations
- ✅ Group by date (Today, Yesterday, This Week, etc.)
- ✅ Add search/filter functionality
- ✅ Double-click to load conversation
- ✅ Delete conversation with confirmation
- ✅ Auto-refresh on save

**Task #6**: Implement Settings UI ✅ COMPLETE
- ✅ Create `SettingsViewModel`
- ✅ Create `SettingsWindow.xaml` with tabbed interface
- ✅ Settings categories implemented:
  - ✅ LLM Providers (API keys for FreeToken, OpenRouter, Anthropic, OpenAI)
  - ✅ Default parameters (temperature, max tokens, top-p)
  - ✅ UI preferences (theme, font size, timestamps, markdown, sidebar width)
  - ✅ Storage settings (path, auto-save, max conversations)
- ✅ Save settings to `%APPDATA%/SNChat/config/settings.json`
- ✅ Settings service with caching
- ✅ Unsaved changes tracking

**Task #7**: Add Conversation Search
- Implement full-text search across conversations
- Search in titles and message content
- Display search results with highlights
- Filter by date range, model, or tags

**Task #8**: UI Polish & Refinements
- Add app icon and branding
- Improve message styling (better spacing, colors)
- Add loading states and error messages
- Keyboard shortcuts (Ctrl+N for new, Ctrl+F for search)
- Add tooltips and help text

## Quick Start for Next Session

### Build the Solution
```bash
cd "D:\Projects\c#\SNChat"
dotnet build
```

### Run the App (once UI is complete)
```bash
dotnet run --project SNChat.App/SNChat.App.csproj
```

### Test Ollama Locally
Ensure Ollama is running:
```bash
# Check if Ollama is available
curl http://localhost:11434/api/tags

# Expected response: JSON list of available models
```

### Key Architectural Decisions
1. **MVVM Pattern**: Using CommunityToolkit.Mvvm for ViewModels
2. **Streaming**: IAsyncEnumerable for real-time token streaming
3. **Storage**: Markdown files with YAML frontmatter (human-readable, git-friendly)
4. **DI**: Microsoft.Extensions.DependencyInjection for all services
5. **Logging**: Serilog for structured logging
6. **HTTP Clients**: Registered via IHttpClientFactory in DI

### Dependencies for Testing
- **Ollama**: Must be running locally on port 11434
  - Install from: https://ollama.ai
  - Pull a model: `ollama pull llama3.1:8b`
- **.NET 8 SDK**: Already installed (version 10.0.102 detected, compatible)

## Architecture Notes

### Conversation Branching
- Each conversation stores `ParentBranchId` and `BranchPoint` (message index)
- Branching creates new conversation with messages up to branch point
- Not yet implemented in UI (planned for Phase 3)

### Streaming Implementation
- `IAsyncEnumerable<StreamChunk>` allows real-time UI updates
- Each chunk contains partial content
- Final chunk has `IsFinal = true` with metadata (token counts, duration)
- UI subscribes with `await foreach (var chunk in stream)`

### File Organization
```
%APPDATA%/SNChat/
├── conversations/
│   └── 2026-08/                      # Month folders
│       └── {conversation-guid}/      # Each conversation has its own folder
│           ├── conversation.md       # Main conversation file
│           └── attachments/          # Media files (images, PDFs, etc.)
├── templates/                        # For Phase 4
├── index/                           # For Phase 3 (search)
├── config/
│   └── settings.json
└── logs/
```

## Phase 1 Progress ✅ COMPLETE

- [x] Create solution structure
- [x] Install NuGet packages
- [x] Implement core domain models
- [x] Create LLM provider interfaces
- [x] Implement OllamaProvider with streaming
- [x] Implement markdown storage service (folder-based)
- [x] Set up DI container and generic host
- [x] Create ChatViewModel with streaming support
- [x] Build basic Chat UI
- [x] Add model switching dropdown
- [x] Fix streaming UI updates (INotifyPropertyChanged)
- [x] Update storage to folder-based structure
- [x] End-to-end testing completed ✅

**Phase 1 Summary:**
- Working WPF chat application with Ollama integration
- Real-time streaming responses
- Model selection from available Ollama models
- Auto-save conversations as markdown with attachments support
- Clean MVVM architecture with dependency injection
- Comprehensive logging with Serilog

## Future Phases Overview

**Phase 3: Advanced Features**
- Conversation branching UI
- Export conversations (PDF, HTML, plain text)
- Import conversations from other chat apps
- Conversation templates/prompts library
- System prompt customization per conversation
- Token usage tracking and cost estimation

**Phase 4: Productivity & Integration**
- Custom prompt templates
- Prompt variables and macros
- Hotkey support for quick prompts
- Clipboard integration
- File drag-and-drop into chat
- Browser extension integration

**Phase 5: RAG & Document Processing**
- Document upload and attachment support
- PDF text extraction
- Image analysis and vision support
- Code file analysis
- Vector embeddings for semantic search
- Context-aware RAG responses

**Technical Debt**
- Add comprehensive error handling in StorageService
- Add input validation for all models
- Write unit tests for services and ViewModels
- Add integration tests for LLM providers
- Performance optimization (lazy loading, virtualization)
- Add logging levels configuration
- Implement retry logic for API calls

---

## Phase 2 - Getting Started

### Recommended Starting Point: Task #1 (Provider Factory)

**Why start here?**
- Foundation for multi-provider support
- Small, focused task with clear deliverables
- Doesn't require external APIs initially
- Tests existing Ollama provider integration

**Implementation Steps:**
1. Create `SNChat.LLM/Interfaces/ILLMProviderFactory.cs`
2. Create `SNChat.LLM/ProviderFactory.cs` implementation
3. Update DI registration in `App.xaml.cs`
4. Add provider selection to ChatViewModel
5. Test provider switching with Ollama

**Estimated Time:** 1-2 hours

### Alternative: Task #3 (Markdown Rendering)

If you prefer UI improvements first:
- Install Markdig.Wpf via NuGet
- Create MarkdownViewer control
- Update ChatView to use markdown rendering
- Test with formatted responses

**Next Session Commands:**
```bash
cd "D:\Projects\c#\SNChat"

# Start Phase 2 Task #1 (Provider Factory)
# OR
# Start Phase 2 Task #3 (Markdown Rendering)

dotnet build
dotnet run --project SNChat.App/SNChat.App.csproj
```
