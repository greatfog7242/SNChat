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

            // Initialize data directories
            InitializeDirectories(appDataPath);

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

        // Register core services
        services.AddSingleton<IStorageService, StorageService>();

        // Register LLM providers
        services.AddSingleton<OllamaProvider>();
        services.AddSingleton<FreeTokenProvider>(sp =>
        {
            var httpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(FreeTokenProvider));
            var logger = sp.GetRequiredService<ILogger<FreeTokenProvider>>();
            // TODO: Load API key from settings
            return new FreeTokenProvider(httpClient, logger, apiKey: "", baseUrl: null);
        });

        // Register provider factory
        services.AddSingleton<ILLMProviderFactory>(sp =>
        {
            var factory = new ProviderFactory();
            factory.RegisterProvider("Ollama", sp.GetRequiredService<OllamaProvider>());
            factory.RegisterProvider("FreeToken", sp.GetRequiredService<FreeTokenProvider>());
            return factory;
        });

        // Register ViewModels
        services.AddTransient<ViewModels.ChatViewModel>();

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

