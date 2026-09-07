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
using SNChat.LLM.Services;
using SNChat.LLM.Tools;
using SNChat.BuildTools;
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
        // The address is set by the provider from settings, since it can point
        // at another machine on the network rather than this one.
        services.AddHttpClient<OllamaProvider>(client =>
        {
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
        services.AddSingleton<IGroupService, GroupService>();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<TemplateService>();
        services.AddSingleton<ProjectService>();

        // Standing instructions from RULES.md, globally and per project.
        services.AddSingleton<RulesService>();

        // How the assistant reports it has finished, and the way back before it
        // works unattended.
        services.AddSingleton<AgentSignals>();
        services.AddSingleton<GitCheckpointService>();
        services.AddSingleton<TaskCompleteTool>();

        // Which project the open conversation is working in. A singleton because
        // the tools are built once at startup and need to read it per call.
        services.AddSingleton<ProjectContext>();

        // Which provider and model the conversation is on, for the same reason:
        // a subagent has to run on something, and there is no path from inside a
        // tool call back to the conversation that made it.
        services.AddSingleton<ActiveModel>();

        services.AddSingleton<AgentDefinitionService>();

        // Folders the user has allowed for this session only, and the dialog
        // that asks. Not saved: a grant answers "may I read this, now", and
        // should not still be in force next week.
        services.AddSingleton<SessionAccessGrants>();
        services.AddSingleton<IAccessPrompt, Services.DialogAccessPrompt>();

        // Constructed by hand because it is part of a cycle: the tool needs the
        // registry to know what it may delegate, the registry factory registers
        // the tool, and the providers are built from the registry. Passing
        // functions defers both resolutions until after everything is built.
        services.AddSingleton(sp => new RunSubagentTool(
            sp.GetRequiredService<AgentDefinitionService>(),
            () => sp.GetRequiredService<ILLMProviderFactory>(),
            () => sp.GetRequiredService<IToolRegistry>(),
            sp.GetRequiredService<ActiveModel>(),
            sp.GetRequiredService<ILogger<RunSubagentTool>>()));

        services.AddSingleton<IImageResizer, Services.WpfImageResizer>();
        services.AddSingleton<AttachmentService>();

        // Folds a long conversation's older messages into a summary, so the
        // history keeps fitting in the model's context window.
        services.AddSingleton<ConversationCompactor>();

        services.AddHttpClient<WebImageCacheService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });

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

        // Build and test tools for the toolchains already on the machine.
        // Registered unconditionally, but they refuse to run until project
        // folders are allowed in Settings - see BuildToolSettings.
        services.AddSingleton<ProcessRunner>();
        services.AddSingleton<ListProjectsTool>();
        services.AddSingleton<BuildProjectTool>();
        services.AddSingleton<RunTestsTool>();
        services.AddSingleton<RunProgramTool>();
        services.AddSingleton<GitStatusTool>();
        services.AddSingleton<GitCommitTool>();

        // Skills: prompt templates the user has marked invocable.
        services.AddSingleton<ListSkillsTool>();
        services.AddSingleton<UseSkillTool>();

        // Reads this app's own log so the assistant can find out why one of its
        // own tool calls was refused. The folder is fixed here rather than taken
        // from the model, so the tool has no path argument at all.
        services.AddSingleton(_ => new ReadAppLogTool(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SNChat", "logs")));

        services.AddSingleton<IToolRegistry>(sp =>
        {
            var registry = new ToolRegistry(sp.GetRequiredService<ILogger<ToolRegistry>>());

            // WebSearchTool stays unregistered. Its backends are gone - Bing's
            // API retired in 2025, Google's Custom Search JSON API is closed to
            // new projects and ends in January 2027, and the DuckDuckGo endpoint
            // only ever answered for encyclopedic entities - and the model
            // cannot tell a dead tool from the working MCP search, so it spent
            // its budget on calls that could not succeed. Web search comes from
            // an MCP server instead; see MCP_AND_SEARCH_RUNBOOK.md.
            //
            // ImageSearchTool is registered, because that reasoning never
            // applied to it: Wikimedia Commons needs no key and still answers,
            // and it falls back there when Google is unset or out of quota.
            // Without it the model has no way to find a picture and answers
            // anyway - inventing Wikimedia URLs that are correctly formed and
            // point at nothing. Nothing else here can search for an image.
            registry.Register(sp.GetRequiredService<ImageSearchTool>());

            // Always available: it reads only this app's own log and can act on
            // nothing, and it is how the assistant finds out why one of its own
            // calls was refused.
            registry.Register(sp.GetRequiredService<ReadAppLogTool>());

            // Delegation, offered only when there is somebody to delegate to.
            // Its description lists the available agents, so registering it with
            // none would spend context advertising an empty menu.
            var agents = sp.GetRequiredService<AgentDefinitionService>();
            agents.SeedDefaultsOnFirstRun();

            if (agents.HasAny())
                registry.Register(sp.GetRequiredService<RunSubagentTool>());

            // Skills are offered only when at least one template is marked
            // invocable. Two tool definitions are sent on every request, so
            // offering them while there is nothing to list spends context on
            // an empty answer.
            if (sp.GetRequiredService<TemplateService>().HasInvocableTemplates())
            {
                registry.Register(sp.GetRequiredService<ListSkillsTool>());
                registry.Register(sp.GetRequiredService<UseSkillTool>());
            }

            // Build tools are registered only once there is somewhere they may
            // work - a folder allowed in Settings, or a project. Their
            // definitions are sent with every request, so offering them while
            // they can only ever refuse would spend context on nothing and
            // invite the model to keep trying them.
            //
            // Decided once at startup, so adding the first project needs a
            // restart before the tools appear. The Settings tab says so.
            var buildTools = sp.GetRequiredService<SettingsService>().GetCachedSettings().BuildTools;
            var hasSomewhereToWork = buildTools.AllowedRoots.Count > 0
                                     || sp.GetRequiredService<ProjectService>().HasAnyProjects();

            if (hasSomewhereToWork)
            {
                registry.Register(sp.GetRequiredService<ListProjectsTool>());
                registry.Register(sp.GetRequiredService<BuildProjectTool>());

                if (buildTools.AllowTests)
                    registry.Register(sp.GetRequiredService<RunTestsTool>());

                if (buildTools.AllowRun)
                    registry.Register(sp.GetRequiredService<RunProgramTool>());

                // Only meaningful where there is a project to work in, which is
                // the same condition as the rest of these.
                registry.Register(sp.GetRequiredService<TaskCompleteTool>());

                // Seeing what changed and committing it, and nothing else. No
                // push, no reset - see BuildToolSettings.AllowCommit.
                if (buildTools.AllowCommit)
                {
                    registry.Register(sp.GetRequiredService<GitStatusTool>());
                    registry.Register(sp.GetRequiredService<GitCommitTool>());
                }
            }

            return registry;
        });

        // MCP (Model Context Protocol) service for external tool servers
        services.AddSingleton<Services.McpService>();

        // Register LLM providers
        services.AddSingleton<OllamaProvider>(sp =>
        {
            var settingsService = sp.GetRequiredService<SettingsService>();

            return new OllamaProvider(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(OllamaProvider)),
                sp.GetRequiredService<ILogger<OllamaProvider>>(),
                sp.GetRequiredService<IToolRegistry>(),
                settingsService.GetCachedSettings().Providers.OllamaBaseUrl,
                // Read per request, not captured, so changing it in Settings
                // applies without a relaunch. The base URL above is the
                // exception - it fixes the HttpClient's address.
                () => settingsService.GetCachedSettings().Providers.OllamaContextWindow);
        });
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

