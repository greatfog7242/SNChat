# SNChat Implementation Status

**Last Updated**: 2026-09-06  
**Phase 1 / Phase 2**: ✅ COMPLETE (history below, from 2026-08-30)  
**Current work**: turning the app into a coding agent — see "Where things stand" next  
**Repository**: https://github.com/greatfog7242/SNChat

---

# Where things stand (read this first)

## Branch and commits

Working branch `cache-search-images`, **ahead of origin and not pushed**. Newest first:

| Commit | What |
|---|---|
| `61acf6a` | Tool calls and results persisted — stage 4's prerequisite |
| `beef193` | Conversation remembers its instructions; rules and skills editors |
| `40259b6` | Frontmatter split fix — conversations stopped being unreadable |
| `c4a9149` | Standing rules from RULES.md, and skills the model can invoke |
| `f7d7f55` | Python, Node, Maven and Ruby; scripts in `run_program`; Kotlin diagnostics |
| `ef1a0f0` | Project picker in the toolbar and a Projects tab in Settings |
| `95883e4` | `read_app_log`, `run_tests` filter, tool-name collision warning |
| `5651b12` | Projects, and `run_program` — the assistant can run what it builds |
| `2e88414` | Tool arguments keep their shape (fixed `edit_file` failing 100% of the time) |
| `78af4eb` | Build and test tools (`list_projects`, `build_project`, `run_tests`) |
| `8b87192` | Drag a group header to reorder it, click to fold |
| `22f7a96` | Context window meter + auto-compaction |
| `d7f99d8` | Per-mode standing instructions (Chat/Coding/Scientific) |

## Uncommitted work in the tree

Nothing. Everything is committed as of `61acf6a`.

**The published `publish\SNChat.App.exe` is stale** — built 2026-09-05 23:30, before the
frontmatter fix, so it still fails to load conversations whose title contains three hyphens.
Republish before judging any conversation-loading behaviour from it.

**Not yet pushed.** Run `git log --oneline origin/cache-search-images..HEAD` for what is
pending — a fixed number written here goes stale the moment another commit lands.

## Where to pick up

**Foundation and Stages 1–3 are done, and Stage 4's prerequisite is now in.** Tool calls and
results persist with the conversation, so a loop can see what its own tools returned.

**Next is the loop itself.** The seam is `ChatViewModel.SendMessageAsync`, right after
`GenerateResponseAsync` returns: at that moment `IsStreaming` is already false, the token
source is disposed, the reply is saved and the meter has refreshed. Hazards to handle, all
listed with line references in `DEVELOPMENT_PLAN.md`:

- `IsStreaming` is false *between* iterations, so the Send button re-enables and the user
  can start a concurrent turn. Needs a separate `IsAgentRunning` flag.
- A cancelled turn leaves the assistant message in `Messages` but not in
  `CurrentConversation.Messages`; the two collections diverge.
- Cancellation is not observable afterwards — the token source is nulled in the `finally`.
- `AutoCompactIfNeededAsync` itself calls the provider and saves; re-entering races it.
- `_measuredPromptTokens` / `_measuredThrough` / `_requestedMessageCount` are single-slot
  fields that overlapping turns corrupt.

Also needed: a `task_complete` tool so stopping is observable rather than string-matched out
of prose, budgets from the project (`MaxLoopIterations`, `MaxLoopMinutes`), the git
checkpoint before a full-auto run, and a real progress surface — `StatusMessage` is one
unstructured string with no iteration counter, and tool results are never shown at all.

Still unverifiable on this machine: Maven and Ruby/Rails, neither being installed.

## Commands that matter

```bash
dotnet build SNChat.slnx
dotnet test SNChat.Tests/SNChat.Tests.csproj      # 301 passing as of 2026-09-06

# Publish: single file. IncludeNativeLibrariesForSelfExtract is NOT optional -
# without it five native WPF DLLs land beside the exe and it is not single-file.
# Close the running app first; it locks publish\SNChat.App.exe.
dotnet publish SNChat.App/SNChat.App.csproj -c Release -r win-x64 \
  --self-contained true -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

**Run the test suite with the registry-only PATH**, not from a developer command prompt —
the latter hides an entire class of bug (see the Visual Studio finding below):

```powershell
powershell -NoProfile -Command "$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User'); dotnet test SNChat.Tests/SNChat.Tests.csproj"
```

## Conventions in this repo

- **Tests**: xUnit, prose sentence names (`A_compacted_message_is_still_compacted_when_it_is_read_back`).
  Comments say *why* the case matters, not what the code does.
- **Comments**: explain the reason and the failure that motivated the code. See
  `WorkspaceGuard.cs` or `ContextMeter.cs` for the house style.
- **Commits**: one feature each, a descriptive sentence in the imperative
  ("Let the assistant build and test your own projects"), body explains the non-obvious
  parts. End with `Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>`.
- **Settings** live in `%APPDATA%\SNChat\config\settings.json`; conversations are markdown
  with YAML frontmatter under `%APPDATA%\SNChat\conversations`.

## Environment facts for this machine

- Ollama at `localhost:11434`; models `orcarouter/Qwen3.8-27B-Uncensored:latest` (262144
  context), `qwen2.5:7b` (32768), `gemma4:latest` (131072).
- Visual Studio 18 Community **and** Build Tools are both installed. `cmake`, `ctest`,
  `ninja` and `MSBuild` live inside them and are **not on the PATH**.
- `JAVA_HOME` is set persistently (machine and user) to Android Studio's bundled JBR;
  `java` is **not** on the PATH. Gradle should inherit it without discovery.
- MCP servers are hand-configured in settings.json: filesystem scoped to `C:\ai-playground`,
  and searxng at `localhost:8888`. ~19 MCP tools, costing roughly 16k prompt tokens.
- Test projects in `C:\ai-playground`: `well_done` (C++/CMake), `FolderTree` (.NET),
  `AndroidFileFinder` (real Gradle/Kotlin project, useful for testing the untested Android path).
- Conversations that used to fail to load now load. They were never corrupt: see the
  frontmatter finding below. Loaded count went from 36 to 42.

## What is verified, and what is only written to spec

Do not trust "it compiles" as evidence. This list says what has actually been run against
the real thing, because more than one bug this week survived a green build.

| Path | State |
|---|---|
| .NET build/test (`dotnet build`, `dotnet test`) | **Verified** — real builds, including a deliberate `CS0103` parsed back with file and line |
| C++ / CMake / MSVC | **Verified** end-to-end, including with the registry-only PATH and with a real `C3861` error |
| Visual Studio tool discovery (`ToolchainLocator`) | **Verified** — resolves cmake from the VS install with cmake absent from PATH |
| Ollama context length from `/api/show` | **Verified** against all three installed models |
| Python build/run (compileall, script + args + stdin) | **Verified** against real Python 3.14 |
| Node detection + npm resolution | **Verified** — npm resolves as `npm.cmd` |
| Java lookup via `JAVA_HOME` | **Verified** — JDK 21 found with `java` absent from PATH |
| **Maven, Ruby/Rails** | **NEVER RUN.** Neither `mvn` nor `ruby` is installed here; commands written to spec |
| Tool argument arrays (`ToolArgumentReader`) | **Verified** — probed the real MCP server, and asserted the JSON-RPC payload shape |
| `run_program` / stdin / arguments | **Verified** — real MSVC-built C++ programs run, stdin fed and drained, args with spaces preserved |
| **Gradle / Kotlin / Android** | **Verified** — `gradlew assembleDebug` builds AndroidFileFinder using the Android Studio JBR via `JAVA_HOME`; a deliberate Kotlin error was captured and is now parsed |
| `GoogleWebSource` / `GoogleImageSource` | Complete and wired, **never exercised** against a successful response — the API appears closed to new projects |
| OpenRouter provider | Argument handling fixed alongside Ollama's but **not re-tested live** after that change |

Test count is **301 passing** at `61acf6a`. If your count is lower, check you are on that
commit before assuming you broke something.

Also stale and not to be trusted: `README.md`, `SESSION_SUMMARY.md`, `CHANGELOG.md` all
date to 2026-08-30. In this file, trust the section you are reading plus
*"Non-obvious findings"*; everything from *"Quick Start (historical)"* onward is history.

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

**Tasks #4, #7, #8**: Phase 2 completion ✅
- Full-text conversation search across message bodies, not just titles, with a
  context snippet showing where the hit is. Message text is flattened and
  lower-cased once at load so filtering stays a substring scan.
- Copy buttons: one per fenced code block, plus per-message "Copy" and
  "Copy code". Clipboard writes are guarded - Clipboard.SetText throws when
  another process holds the clipboard.
- Keyboard shortcuts: Ctrl+N (new conversation), Ctrl+F (focus search),
  registered in code-behind because the window has no view model of its own.
- Deferred: app icon (still the stock WPF icon; needs a supplied .ico or PNG).

### Non-obvious findings (worth not rediscovering)

#### From 2026-09-05/06 (context meter, build tools, tool arguments)

**Frontmatter was split on the substring "---", which silently lost conversations.**
`content.Split("---")` splits wherever those three characters appear, not on delimiter
lines. A conversation whose first message carried an attachment is auto-titled
`--- Attached image: photo.jpg ---`; YAML writes that as a folded scalar across several
lines; the reader then parsed frontmatter only as far as the first hyphens, `created` went
missing, and the file threw `KeyNotFoundException` forever. The files on disk were written
perfectly well - the reader was broken - and six conversations were sitting unreadable.
These had been dismissed as "corrupt, pre-existing, harmless" three times in one session
before anyone actually opened one. `MarkdownDocument.TrySplit` now splits on delimiter
lines; `TemplateService` and `ProjectService` had copied the same broken split. A first
fix that trimmed both ends was still wrong: YAML indents a block scalar's content by two
spaces, so a value containing three hyphens yields a line reading `  ---`, which trimming
turned back into a delimiter. A delimiter is a line at column zero and nothing else. Caught
only because a test round-tripped a multi-line system prompt through real YAML.

**The Kotlin compiler's diagnostics look like nothing else.** A real Android build prints
`e: file:///C:/app/src/.../Thing.kt:6:17 Unresolved reference 'foo'.` — a bare `e:` instead
of the word error, a `file://` URI instead of a path, and no colon before the message. None
of the existing patterns matched it, so every Kotlin error was invisible and the model was
told only that the build failed. Found by adding a deliberately broken file to
`AndroidFileFinder` and reading the real output; there is now a test using that exact line.
Paths are percent-decoded, since any project under a folder with a space arrives with %20.

**Visual Studio bundles cmake, ctest, ninja and MSBuild and puts none of them on the
PATH.** A developer command prompt adds them, so they look installed when you check from
one — but the app is launched from Explorer and inherits the plain registry PATH, where a
bare `cmake` fails with "the system cannot find the file specified" on a machine that
plainly has CMake. `ToolchainLocator` resolves: explicit setting → PATH → every Visual
Studio installation. **Use `vswhere -all`, never `-latest`**: on this machine `-latest`
returns the Build Tools install, not the Community one. The compiler needs no such help —
CMake's Visual Studio generator locates MSVC through the installation, so a build works
with no developer environment once cmake itself is found (verified with the registry-only
PATH). Corollary: **always run the test suite with the registry-only PATH**, or this whole
class of bug is invisible.

**Tool arguments that are arrays or objects were flattened to strings.** Both providers
unpacked model tool-call arguments with `_ => property.Value.GetRawText()`, so an array
arrived at the tool as a *string of JSON*. The MCP filesystem server answers
`-32602 Invalid input: expected array, received string at edits`, so `edit_file` failed
**100% of the time** while every tool taking only scalars worked perfectly. That pattern
reads exactly like a model too weak to use the tool — it is not. A 100% failure rate is
structural; model weakness fails intermittently. Fixed in
`SNChat.LLM/Providers/Base/ToolArgumentReader.cs`. Note the related trap in the same code:
`TryGetInt64(out var l) ? l : GetDouble()` has common type `double`, so whole numbers get
widened — an explicit `(object)` cast is required.

**MCP `edit_file` is forgiving, so a rejection usually means a shape problem.** Probed
directly: wrong indentation, trailing whitespace, and either line ending are all accepted.
It only refuses when `oldText` genuinely is not in the file. So if edits fail, suspect the
argument shape before suspecting the model's copying.

**Ollama's model list reported a hardcoded 4096 context for every model.** The real length
comes from `POST /api/show`, under `model_info` at a key ending `.context_length` (the
prefix is the architecture, e.g. `qwen35.context_length`). On this machine that is 262144
versus the hardcoded 4096 — a 64× error, which drove the context meter to 100% on the first
message and triggered a needless compaction. `num_ctx` is now also settable, which pins
what Ollama actually serves so the meter and the request agree.

**Tool definitions dominate the prompt.** Nineteen MCP tools cost ~16k tokens against ~130
for the message that prompted them. Any context estimate that ignores them reads near zero
until the provider's first real count arrives, then jumps to full. `TokenEstimator.EstimateTools`
counts them; they must **not** be added on top of a provider-measured prompt, which already
includes them.

**`ConversationMetadata.CustomData` is never serialised.** It exists on the model but
`StorageService.GenerateMarkdown` does not write it, so anything put there is silently lost.
Add real frontmatter fields instead.

**`ToolRegistry.Register` is last-write-wins on a case-insensitive name, and MCP tools
register *after* the built-ins.** An MCP server exposing `build_project` would silently
shadow ours with no warning.

**A cancelled turn diverges the two message collections.** `ChatViewModel` adds the
assistant message to `Messages` (the view) immediately, but to
`CurrentConversation.Messages` only on success. After a cancel the view has a message the
conversation does not, and it is never saved.

**Nested scroll viewers block the mouse wheel.** Every message renders a
MarkdownViewer, which contains its own FlowDocumentScrollViewer > ScrollViewer.
That inner scroller marks the bubbling MouseWheel event handled, so the message
list never scrolled. Fixed by handling the tunnelling PreviewMouseWheel on the
outer list, which fires first. Verified A/B: outer offset stayed 0 without the
fix and moved 240 with it. The same trap is why generated code blocks wrap
instead of scrolling horizontally.

**Code blocks have no renderer hook, but the document is reachable.** Markdig
emits a fenced block as a plain Paragraph with no marker, so buttons cannot be
attached during rendering. MarkdownViewer.Document is a public settable
DependencyProperty, so CodeBlockCopyBehavior watches it and rewrites each code
paragraph in place. Post-processing rather than replacing the pipeline keeps
Markdig's own image and hyperlink styling applied. Code paragraphs are
identified by having a monospace FontFamily AND a non-null Style; prose has
neither.

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

## Quick Start (historical — written 2026-08-30, kept for the architecture notes)

> Superseded. For current commands, state and next steps see
> **"Where things stand (read this first)"** at the top of this file. The task suggestions
> in this section refer to Phase 2 work that has since shipped.

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
