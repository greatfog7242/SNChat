using System.IO;
using System.Net.Http;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Serilog;
using SNChat.Core.Interfaces;
using SNChat.Core.Services;
using SNChat.LLM;
using SNChat.LLM.Interfaces;
using SNChat.LLM.Providers.Ollama;
using SNChat.LLM.Providers.FreeToken;
using SNChat.LLM.Providers.OpenRouter;
using SNChat.Core.Tools;
using SNChat.WebTools;
using SNChat.WebTools.ImageSources;
using SNChat.WebTools.WebSources;

namespace SNChat.App;

public partial class App : Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Configure Serilog
        var appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SNChat"
        );
        var logPath = Path.Combine(appDataPath, "logs", "snchat-.log");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(logPath, rollingInterval: RollingInterval.Day)
            .CreateLogger();

        try
        {
            // Build and start the host
            _host = Host.CreateDefaultBuilder()
                .UseSerilog()
                .ConfigureServices((context, services) =>
                {
                    ConfigureServices(services);
                })
                .Build();

            await _host.StartAsync();

            // Load settings early
            var settingsService = _host.Services.GetRequiredService<SettingsService>();
            await settingsService.LoadSettingsAsync();

            // Initialize data directories
            InitializeDirectories(appDataPath);

            // Initialize MCP servers and register their tools
            var mcpService = _host.Services.GetRequiredService<Services.McpService>();
            await mcpService.InitializeAsync();

            // Show the main window
            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application failed to start");
            MessageBox.Show($"Failed to start application: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    private void ConfigureServices(IServiceCollection services)
    {
        // Register HttpClients for LLM providers
        services.AddHttpClient<OllamaProvider>(client =>
        {
            client.BaseAddress = new Uri("http://localhost:11434");
            client.Timeout = TimeSpan.FromMinutes(5);
        });

        services.AddHttpClient<FreeTokenProvider>(client =>
        {
            client.Timeout = TimeSpan.FromMinutes(5);
        });

        services.AddHttpClient<OpenRouterProvider>(client =>
        {
            client.Timeout = TimeSpan.FromMinutes(5);
        });

        // Register core services
        services.AddSingleton<IStorageService, StorageService>();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<TemplateService>();
        services.AddSingleton<IImageResizer, Services.WpfImageResizer>();
        services.AddSingleton<AttachmentService>();

        // Tools the model can invoke
        services.AddHttpClient<WebSearchTool>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddHttpClient<GoogleWebSource>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        // Image search backends; which one runs is chosen in Settings.
        services.AddHttpClient<CommonsImageSource>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddHttpClient<GoogleImageSource>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddSingleton<ImageSearchTool>();

        services.AddSingleton<IToolRegistry>(sp =>
        {
            var registry = new ToolRegistry(sp.GetRequiredService<ILogger<ToolRegistry>>());

            // WebSearchTool and ImageSearchTool are deliberately not registered.
            // Every backend they can reach is gone: Bing's API retired in 2025,
            // Google's Custom Search JSON API is closed to new projects and ends
            // in January 2027, and the DuckDuckGo endpoint only ever answered
            // for encyclopedic entities. They return nothing, but the model
            // cannot tell them apart from the working MCP search and has been
            // seen calling both in one turn, spending its tool budget on calls
            // that cannot succeed. Search now comes from an MCP server; see
            // MCP_AND_SEARCH_RUNBOOK.md. They stay in the container so
            // re-registering is a one-line change if a backend revives.
            return registry;
        });

        // MCP (Model Context Protocol) service for external tool servers
        services.AddSingleton<Services.McpService>();

        // Register LLM providers
        services.AddSingleton<OllamaProvider>(sp => new OllamaProvider(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(OllamaProvider)),
            sp.GetRequiredService<ILogger<OllamaProvider>>(),
            sp.GetRequiredService<IToolRegistry>()));
        services.AddSingleton<FreeTokenProvider>(sp =>
        {
            var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(FreeTokenProvider));
            var logger = sp.GetRequiredService<ILogger<FreeTokenProvider>>();
            var settingsService = sp.GetRequiredService<SettingsService>();
            var settings = settingsService.GetCachedSettings();
            return new FreeTokenProvider(httpClient, logger,
                apiKey: settings.Providers.FreeTokenApiKey,
                baseUrl: string.IsNullOrEmpty(settings.Providers.FreeTokenBaseUrl)
                    ? null
                    : settings.Providers.FreeTokenBaseUrl);
        });

        services.AddSingleton<OpenRouterProvider>(sp =>
        {
            var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(OpenRouterProvider));
            var logger = sp.GetRequiredService<ILogger<OpenRouterProvider>>();
            var settingsService = sp.GetRequiredService<SettingsService>();

            // Read on each request rather than captured here: this provider is a
            // singleton created at startup, so a key or model selection saved in
            // Settings would otherwise not apply until the app was relaunched.
            // The base URL is the exception - it fixes the HttpClient's address.
            return new OpenRouterProvider(httpClient, logger,
                sp.GetRequiredService<IToolRegistry>(),
                () =>
                {
                    var providers = settingsService.GetCachedSettings().Providers;
                    return new OpenRouterRuntimeOptions
                    {
                        ApiKey = providers.OpenRouterApiKey,
                        ByokProviders = providers.OpenRouterByokProviders,
                        SelectedModels = providers.OpenRouterSelectedModels
                    };
                },
                baseUrl: string.IsNullOrEmpty(settingsService.GetCachedSettings().Providers.OpenRouterBaseUrl)
                    ? null
                    : settingsService.GetCachedSettings().Providers.OpenRouterBaseUrl);
        });

        // Register provider factory
        services.AddSingleton<ILLMProviderFactory>(sp =>
        {
            var factory = new ProviderFactory();
            factory.RegisterProvider("Ollama", sp.GetRequiredService<OllamaProvider>());
            factory.RegisterProvider("FreeToken", sp.GetRequiredService<FreeTokenProvider>());
            factory.RegisterProvider("OpenRouter", sp.GetRequiredService<OpenRouterProvider>());
            return factory;
        });

        // Register ViewModels
        services.AddTransient<ViewModels.ChatViewModel>();
        services.AddTransient<ViewModels.ConversationListViewModel>();
        services.AddTransient<ViewModels.SettingsViewModel>();
        services.AddTransient<ViewModels.TemplatePickerViewModel>();

        // Register Views
        services.AddSingleton<MainWindow>();
    }

    private void InitializeDirectories(string appDataPath)
    {
        var directories = new[]
        {
            appDataPath,
            Path.Combine(appDataPath, "conversations"),
            Path.Combine(appDataPath, "logs"),
            Path.Combine(appDataPath, "config"),
            Path.Combine(appDataPath, "templates"),
            Path.Combine(appDataPath, "attachments"),
            Path.Combine(appDataPath, "index")
        };

        foreach (var dir in directories)
        {
            Directory.CreateDirectory(dir);
        }

        Log.Information("Initialized application directories at {AppDataPath}", appDataPath);
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        Log.CloseAndFlush();
        base.OnExit(e);
    }
}

