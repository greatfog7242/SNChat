# Changelog

All notable changes to SNChat will be documented in this file.

## [Phase 1] - 2026-08-30 - ✅ COMPLETE

### Foundation & Basic Chat

**Added**
- Complete WPF application structure with 7 projects
- Core domain models (Conversation, Message, ConversationMetadata, ModelParameters, Attachment)
- Ollama LLM provider with real-time streaming support
- Folder-based storage service with markdown format
- Chat UI with message display and input
- Model selection dropdown for available Ollama models
- Dependency injection with Microsoft.Extensions.Hosting
- Serilog logging to file
- Auto-save conversations after each response
- Auto-generate conversation titles from first message
- Streaming indicator during response generation
- Cancel button to stop generation mid-stream

**Technical Details**
- MVVM architecture with CommunityToolkit.Mvvm
- `IAsyncEnumerable<StreamChunk>` for streaming
- `INotifyPropertyChanged` for real-time UI updates
- Storage structure: `%APPDATA%/SNChat/conversations/YYYY-MM/{guid}/`
  - `conversation.md` - Main file with YAML frontmatter
  - `attachments/` - Folder for media files

**Fixed**
- Message content not updating during streaming (added INotifyPropertyChanged to Message model)
- Storage updated from single-file to folder-based structure for attachments support

**Dependencies**
- .NET 8.0
- CommunityToolkit.Mvvm 8.4.2
- Microsoft.Extensions.Hosting 10.0.11
- Microsoft.Extensions.Http 10.0.11
- Serilog.Extensions.Hosting 10.0.0
- Serilog.Sinks.File 7.0.0
- YamlDotNet (SNChat.Core)
- Polly (SNChat.LLM)

**Testing**
- ✅ Application builds successfully
- ✅ Real-time streaming works
- ✅ Model switching works
- ✅ Conversations save correctly
- ✅ Ollama integration verified

## [Phase 2] - IN PROGRESS

### Enhanced UI & Multiple Providers

**Added (2026-08-30)**
- ✅ Provider factory pattern (ILLMProviderFactory, ProviderFactory)
- ✅ FreeToken provider with OpenAI-compatible API
- ✅ Provider selection dropdown in toolbar
- ✅ Dynamic model loading per provider
- ✅ Provider name stored in conversation metadata
- ✅ Graceful fallback to default models when API unavailable
- ✅ Markdown rendering in chat (Markdig.Wpf)
- ✅ Formatted text display (headers, bold, italic, code blocks)
- ✅ Code syntax styling with monospace font
- ✅ Support for lists, links, blockquotes, tables

**Added (2026-08-30 - Latest)**
- ✅ Complete Settings UI with tabbed interface
- ✅ API key management for multiple providers
- ✅ Settings storage as JSON
- ✅ Default parameters configuration
- ✅ UI preferences customization
- ✅ Storage settings management
- ✅ Unsaved changes tracking
- ✅ Conversation list sidebar with date grouping
- ✅ Search and filter conversations
- ✅ Load/delete past conversations
- ✅ Bug fix: GUID parsing for empty parent_branch fields

**Added (2026-08-30 - tool calling, unplanned bonus)**
- ✅ Tool-calling framework (`ITool`, `IToolRegistry`, agentic loop in OllamaProvider)
- ✅ `web_search` tool - DuckDuckGo Instant Answer API + Wikipedia fallback
- ✅ `image_search` tool - Wikimedia Commons, returns freely-licensed images
- ✅ Web search toggle in toolbar; live tool-progress status in the overlay
- ✅ Clickable source links (added the missing `RequestNavigate` handler)
- ✅ Inline images in chat responses

**Fixed**
- Ollama 404s caused by a hardcoded `llama3.1:8b` default that was not installed;
  conversations now validate their saved model against what the provider reports
- `request.CancellationToken` was never set, so Cancel relied on a loop check
- Provider errors were swallowed by a bare `yield break`, producing an empty
  message; Ollama's JSON error body is now surfaced to the user
- Markdig.Wpf renders nothing for percent-encoded image URLs (silent zero-sized
  image); URLs are now decoded before embedding
- Oversized images (up to 3840px) now request ~500px thumbnails

**Added (2026-08-30 - selectable search backends)**
- ✅ `GoogleWebSource` and `GoogleImageSource` (Custom Search JSON API)
- ✅ Settings → Search tab: web source, image source, SafeSearch toggle
- ✅ Automatic fallback to keyless sources when a backend is unconfigured,
  over quota, or returns nothing
- ⚠️ Google paths are dormant: the API appears closed to new projects, so they
  are complete but unverified against a live response

**Optional/Future Enhancements**
- [ ] Enhanced code syntax highlighting with AvalonEdit
- [ ] Full-text conversation search across all messages
- [ ] Conversation templates
- [ ] Export conversations (PDF, HTML)
- [ ] Additional LLM providers (OpenRouter direct integration)

**Technical Details**
- FreeToken provider supports GPT-3.5, GPT-4, Claude 3 models
- Provider factory allows easy addition of new providers
- Each provider can have custom settings (API key, base URL)
- Streaming support maintained across all providers

---

## Version History

- **Phase 1** (2026-08-30): Foundation complete - working chat app with Ollama
- **Phase 2** (Starting): Multiple providers and enhanced UI
- **Phase 3** (Planned): Advanced features and branching
- **Phase 4** (Planned): Productivity and integrations
- **Phase 5** (Planned): RAG and document processing
