using Microsoft.Extensions.Logging;
using SNChat.Core.Models;
using SNChat.Core.Services;
using SNChat.Core.Tools;

namespace SNChat.BuildTools;

/// <summary>
/// Builds a project with whichever toolchain it belongs to and hands back the
/// compiler's complaints, so the model can fix a break and check its work.
///
/// The configuration is a fixed choice rather than a free string, and no other
/// arguments are passed through. A build already runs the project's own scripts;
/// there is no reason to widen that into taking flags from the model as well.
/// </summary>
public class BuildProjectTool : ITool
{
    private readonly SettingsService _settingsService;
    private readonly ProjectContext _projects;
    private readonly ProcessRunner _runner;
    private readonly ILogger<BuildProjectTool> _logger;

    public string Name => "build_project";

    public string Description =>
        "Compile a project and return the compiler errors and warnings. Handles " +
        ".NET solutions and projects (MSBuild), C/C++ projects with a " +
        "CMakeLists.txt, and Android/Gradle projects. Use it to check whether " +
        "code compiles and to read the exact errors when it does not. Call " +
        "list_projects first if you do not already know the path.";

    public ToolParameterSchema Parameters => new()
    {
        Properties = new Dictionary<string, ToolParameterProperty>
        {
            ["path"] = new()
            {
                Type = "string",
                Description = "Full path to the solution, project file, or the folder " +
                              "containing one. Must be inside an allowed project folder."
            },
            ["configuration"] = new()
            {
                Type = "string",
                Description = "Which configuration to build. Defaults to Debug.",
                Enum = new List<string> { "Debug", "Release" }
            }
        },
        Required = new List<string> { "path" }
    };

    public BuildProjectTool(
        SettingsService settingsService,
        ProjectContext projects,
        ProcessRunner runner,
        ILogger<BuildProjectTool> logger)
    {
        _settingsService = settingsService;
        _projects = projects;
        _runner = runner;
        _logger = logger;
    }

    public async Task<string> ExecuteAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        var settings = _settingsService.GetCachedSettings().BuildTools;
        var resolved = BuildToolArguments.Resolve(arguments, settings, _projects, out var failure);

        if (resolved == null)
            return failure!;

        var (target, configuration) = resolved.Value;

        _logger.LogInformation("Building {Kind} target {Path} ({Configuration})",
            target.Kind, target.Path, configuration);

        var timeout = TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 10, 3600));
        var what = $"Build of {Path.GetFileName(target.WorkingDirectory)}";

        // CMake is the odd one out: it has to generate a build system before it
        // can build anything, and a first-time project has none.
        if (target.Kind == ProjectKind.CMake)
            return await BuildCMakeAsync(target, configuration, settings, timeout, cancellationToken);

        var (fileName, args) = target.Kind == ProjectKind.Gradle
            ? GradleCommand(target, configuration)
            : DotNetCommand(target, configuration, settings);

        var result = await _runner.RunAsync(
            fileName, args, target.WorkingDirectory, timeout, cancellationToken);

        return BuildOutputParser.Summarize(result, $"Build of {Path.GetFileName(target.Path)}");
    }

    /// <summary>
    /// Configures then builds. Configuring every time rather than only when the
    /// build folder is missing, because it is quick when nothing has changed and
    /// it is what picks up an edited CMakeLists.txt - which is exactly what the
    /// model will have just done.
    /// </summary>
    private async Task<string> BuildCMakeAsync(
        BuildTarget target,
        string configuration,
        BuildToolSettings settings,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var source = target.WorkingDirectory;
        var buildDirectory = target.CMakeBuildDirectory;

        // Usually found inside Visual Studio rather than on the PATH.
        var cmake = ToolchainLocator.FindCMake(settings.CMakePath);

        if (cmake == null)
            return ToolchainLocator.NotFoundMessage("cmake", "cmake executable");

        _logger.LogInformation("Using cmake at {Path}", cmake);

        var configure = await _runner.RunAsync(
            cmake,
            new[]
            {
                "-S", source,
                "-B", buildDirectory,
                // Single-config generators (Ninja, Makefiles) take the
                // configuration here; multi-config ones (Visual Studio) ignore
                // it and take --config at build time instead. Sending both is
                // what makes one command work with either.
                $"-DCMAKE_BUILD_TYPE={configuration}",
                // ...and this stops the multi-config case from warning that the
                // variable went unused, which it otherwise does on every single
                // build, for the model to read and wonder about.
                "--no-warn-unused-cli"
            },
            source,
            timeout,
            cancellationToken);

        // A configure failure means there is nothing to build, and its errors
        // are the ones worth reporting - a missing compiler, or a broken
        // CMakeLists the model has just written.
        if (!configure.Succeeded)
            return BuildOutputParser.Summarize(configure, "CMake configure");

        var build = await _runner.RunAsync(
            cmake,
            new[] { "--build", buildDirectory, "--config", configuration },
            source,
            timeout,
            cancellationToken);

        return BuildOutputParser.Summarize(
            build, $"Build of {Path.GetFileName(source)}");
    }

    private static (string, List<string>) DotNetCommand(
        BuildTarget target,
        string configuration,
        BuildToolSettings settings)
    {
        // MSBuild.exe from Visual Studio when one is configured: "dotnet build"
        // cannot build classic .NET Framework projects at all, and a solution
        // holding even one of them fails without it. Resolved through the
        // locator so that "msbuild" on its own finds the copy inside Visual
        // Studio, which is not on the PATH either.
        if (!string.IsNullOrWhiteSpace(settings.MsBuildPath))
        {
            var msbuild = ToolchainLocator.FindMsBuild(settings.MsBuildPath)
                          ?? settings.MsBuildPath;

            return (msbuild, new List<string>
            {
                target.Path,
                $"/p:Configuration={configuration}",

                "/nologo",
                // Terse, because the model only ever reads the diagnostics and a
                // normal-verbosity log of a large solution is enormous.
                "/verbosity:minimal"
            });
        }

        return (settings.DotnetPath, new List<string>
        {
            "build",
            target.Path,
            "--configuration", configuration,
            "--nologo",
            "--verbosity", "minimal"
        });
    }

    private static (string, List<string>) GradleCommand(BuildTarget target, string configuration)
    {
        // assembleDebug/assembleRelease is the Android convention; plain "build"
        // would also run the test suite, which is a separate tool here.
        var task = configuration.Equals("Release", StringComparison.OrdinalIgnoreCase)
            ? "assembleRelease"
            : "assembleDebug";

        return (BuildToolArguments.GradleExecutable(target), new List<string>
        {
            task,
            "--console=plain",
            // Gradle keeps a daemon alive between builds; a one-shot invocation
            // from a chat turn has nothing to reuse it and it would linger.
            "--no-daemon"
        });
    }
}
