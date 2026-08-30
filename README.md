# SNChat - AI Chat Assistant

A modern WPF desktop application for chatting with LLM models, featuring real-time streaming responses and conversation management.

## Features

✅ **Phase 1 - COMPLETE**
- Real-time streaming chat with Ollama models
- Model selection from available Ollama models
- Auto-save conversations as markdown
- Folder-based storage with attachments support
- Clean MVVM architecture with dependency injection
- Comprehensive logging

🚧 **Phase 2 - IN PROGRESS** (40% Complete)
- ✅ Multiple LLM provider support (Ollama, FreeToken)
- ✅ Provider factory pattern for easy extensibility
- ✅ Markdown rendering in chat (Markdig.Wpf)
- ✅ Formatted text with code blocks, headers, lists, tables
- ⚠️ API key management (needs Settings UI)
- 🔄 Enhanced code syntax highlighting (optional)
- 🔄 Conversation history/list view
- 🔄 Settings UI
- 🔄 Conversation search

## Quick Start

### Prerequisites
- .NET 8 SDK
- [Ollama](https://ollama.ai) installed and running
- At least one Ollama model pulled (e.g., `ollama pull llama3.1:8b`)

### Running the App

```bash
cd "D:\Projects\c#\SNChat"
dotnet build
dotnet run --project SNChat.App/SNChat.App.csproj
```

### Verify Ollama is Running

```bash
curl http://localhost:11434/api/tags
```

## Project Structure

```
SNChat/
├── SNChat.App/              # WPF Application
│   ├── ViewModels/          # MVVM ViewModels
│   ├── Views/               # XAML Views
│   └── Converters/          # Value Converters
├── SNChat.Core/             # Domain Models & Services
│   ├── Models/              # Conversation, Message, etc.
│   ├── Services/            # StorageService
│   └── Interfaces/          # Service interfaces
├── SNChat.LLM/              # LLM Provider Abstraction
│   ├── Interfaces/          # ILLMProvider
│   ├── Models/              # Request/Response models
│   └── Providers/           # Provider implementations
│       └── Ollama/          # OllamaProvider
├── SNChat.FileTools/        # File operations (planned)
├── SNChat.WebTools/         # Web scraping (planned)
├── SNChat.RAG/              # Document processing (planned)
└── SNChat.Tests/            # Unit tests (planned)
```

## Storage Format

Conversations are stored in a folder-based structure:

```
%APPDATA%/SNChat/conversations/
└── YYYY-MM/
    └── {conversation-guid}/
        ├── conversation.md       # Markdown with YAML frontmatter
        └── attachments/          # Media files (Phase 5)
```

Each conversation.md file contains:
- YAML frontmatter with metadata (ID, title, timestamps, model, parameters)
- Markdown-formatted messages with timestamps

## Architecture

### MVVM Pattern
- **Models**: Core domain objects (Conversation, Message, ModelParameters)
- **ViewModels**: Business logic with CommunityToolkit.Mvvm
- **Views**: WPF XAML UI

### Dependency Injection
- Microsoft.Extensions.Hosting for WPF
- Service registration in `App.xaml.cs`
- Constructor injection throughout

### LLM Provider Pattern
- `ILLMProvider` interface for abstraction
- `OllamaProvider` implements streaming via `IAsyncEnumerable<StreamChunk>`
- Provider factory pattern (Phase 2)

### Logging
- Serilog with file sink
- Logs stored in `%APPDATA%/SNChat/logs/`

## Development Roadmap

See [HANDOFF.md](HANDOFF.md) for detailed implementation status and next steps.

### Completed (Phase 1)
- ✅ Solution structure and project setup
- ✅ Core domain models
- ✅ Ollama provider with streaming
- ✅ Folder-based storage service
- ✅ WPF UI with chat interface
- ✅ Model switching
- ✅ DI container and hosting

### Completed (Phase 2 - Partial)
- ✅ Provider factory pattern
- ✅ Multiple provider support (Ollama, FreeToken)
- ✅ Markdown rendering with Markdig.Wpf
- ✅ Rich text formatting (code blocks, headers, lists, tables)
- ✅ Provider/model selection UI

### In Progress (Phase 2)
- 🚧 API key management
- 🚧 Conversation list view
- 🚧 Settings UI
- 🚧 Enhanced code highlighting

### Planned
- **Phase 3**: Branching, export, templates
- **Phase 4**: Productivity features, integrations
- **Phase 5**: RAG, document processing

## Configuration

### Default Settings
- **Temperature**: 0.7
- **Max Tokens**: 2048
- **Top P**: 0.9
- **Ollama Endpoint**: http://localhost:11434

Settings UI coming in Phase 2.

## Keyboard Shortcuts

- `Ctrl+Enter` - Send message
- `Ctrl+N` - New conversation (planned)
- `Ctrl+F` - Search conversations (planned)

## Contributing

See [HANDOFF.md](HANDOFF.md) for current development status and next tasks.

## License

TBD

## Credits

Built with:
- [.NET 8](https://dotnet.microsoft.com/)
- [WPF](https://github.com/dotnet/wpf)
- [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet)
- [Serilog](https://serilog.net/)
- [YamlDotNet](https://github.com/aaubry/YamlDotNet)
- [Ollama](https://ollama.ai/)
