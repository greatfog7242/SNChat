namespace SNChat.BuildTools;

public enum ProjectKind
{
    Unknown,

    /// <summary>A .sln, .slnx, .csproj or similar - built with dotnet or MSBuild.</summary>
    DotNet,

    /// <summary>A Gradle project, as Android Studio produces.</summary>
    Gradle,

    /// <summary>
    /// A CMake project - the usual shape of a C++ project, and what Visual
    /// Studio opens when you point it at a folder rather than a solution.
    /// </summary>
    CMake,

    /// <summary>Maven, from a pom.xml. Gradle Java is <see cref="Gradle"/>.</summary>
    Maven,

    /// <summary>Anything with a package.json - Node, or TypeScript built through it.</summary>
    Node,

    /// <summary>Python, from pyproject.toml, requirements.txt or setup.py.</summary>
    Python,

    /// <summary>Ruby, from a Gemfile. Rails is the usual case.</summary>
    Ruby
}

/// <summary>Something the tools can build, and how.</summary>
public sealed record BuildTarget(ProjectKind Kind, string Path, string Description)
{
    /// <summary>The folder a build command should run in.</summary>
    public string WorkingDirectory =>
        Directory.Exists(Path) ? Path : System.IO.Path.GetDirectoryName(Path) ?? Path;

    /// <summary>
    /// Where CMake should put its generated build system. Kept inside the
    /// project as "build", which is the near-universal convention and is already
    /// inside the allowed folder, so nothing is written anywhere unexpected.
    /// </summary>
    public string CMakeBuildDirectory => System.IO.Path.Combine(WorkingDirectory, "build");
}

/// <summary>
/// Works out what is buildable at a path, and finds what is buildable under one.
/// </summary>
public static class ProjectLocator
{
    private static readonly string[] DotNetProjectExtensions =
        { ".sln", ".slnx", ".csproj", ".vbproj", ".fsproj" };

    /// <summary>
    /// Directories never worth walking into. Skipped for speed - a node_modules
    /// or a Gradle build folder can hold tens of thousands of files - and
    /// because anything found in them is generated rather than a project
    /// somebody would want built.
    /// </summary>
    private static readonly HashSet<string> SkippedDirectories =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "bin", "obj", "node_modules", ".git", ".vs", ".idea", ".gradle",
            "build", "publish", "packages", "target", "__pycache__"
        };

    /// <summary>
    /// What sort of project a path refers to. A file is judged by its extension;
    /// a directory by what it contains, preferring a solution over the projects
    /// inside it so that "build this folder" builds the whole thing at once.
    /// </summary>
    public static BuildTarget? Identify(string path)
    {
        if (File.Exists(path))
        {
            var extension = Path.GetExtension(path);

            if (DotNetProjectExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                return new BuildTarget(ProjectKind.DotNet, path, Path.GetFileName(path));

            if (Path.GetFileName(path).StartsWith("build.gradle", StringComparison.OrdinalIgnoreCase))
                return new BuildTarget(ProjectKind.Gradle, path, Path.GetFileName(path));

            if (Path.GetFileName(path).Equals(CMakeListsName, StringComparison.OrdinalIgnoreCase))
                return new BuildTarget(ProjectKind.CMake, path, Path.GetFileName(path));

            return null;
        }

        if (!Directory.Exists(path))
            return null;

        var solution = FirstMatch(path, "*.sln") ?? FirstMatch(path, "*.slnx");
        if (solution != null)
            return new BuildTarget(ProjectKind.DotNet, solution, Path.GetFileName(solution));

        // Checked before .csproj so an Android project that also carries a
        // stray project file is still treated as the Gradle build it is.
        if (HasGradleWrapper(path) || FirstMatch(path, "build.gradle*") != null)
            return new BuildTarget(ProjectKind.Gradle, path, Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)));

        foreach (var extension in DotNetProjectExtensions)
        {
            var project = FirstMatch(path, "*" + extension);
            if (project != null)
                return new BuildTarget(ProjectKind.DotNet, project, Path.GetFileName(project));
        }

        // Last, because a C++ project that also ships a solution is better built
        // through the solution, and a CMakeLists can sit beside one.
        var cmake = Path.Combine(path, CMakeListsName);

        if (File.Exists(cmake))
            return new BuildTarget(ProjectKind.CMake, cmake, Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)));

        return IdentifyByMarker(path);
    }

    /// <summary>
    /// Languages identified by a single well-known file at the root.
    ///
    /// Order matters where a folder carries more than one marker, which is
    /// common and not a mistake: a Python service with a package.json for its
    /// front-end assets, or a Java project with a requirements.txt for a helper
    /// script. The most specific and least often incidental marker wins, so a
    /// pom.xml beats a package.json and both beat a bare requirements.txt.
    ///
    /// It will still sometimes pick the wrong one. list_projects reports the
    /// kind it decided on so that is visible rather than silent.
    /// </summary>
    private static readonly (string Marker, ProjectKind Kind)[] Markers =
    {
        ("pom.xml", ProjectKind.Maven),
        ("package.json", ProjectKind.Node),
        ("Gemfile", ProjectKind.Ruby),
        ("pyproject.toml", ProjectKind.Python),
        ("setup.py", ProjectKind.Python),
        ("requirements.txt", ProjectKind.Python)
    };

    private static BuildTarget? IdentifyByMarker(string directory)
    {
        var name = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar));

        foreach (var (marker, kind) in Markers)
        {
            var candidate = Path.Combine(directory, marker);

            if (File.Exists(candidate))
                return new BuildTarget(kind, candidate, name);
        }

        return null;
    }

    /// <summary>
    /// Whether a Ruby project looks like Rails, which changes how its tests are
    /// run. Judged by config/application.rb, the file Rails itself boots from.
    /// </summary>
    public static bool IsRails(BuildTarget target) =>
        target.Kind == ProjectKind.Ruby
        && File.Exists(Path.Combine(target.WorkingDirectory, "config", "application.rb"));

    public const string CMakeListsName = "CMakeLists.txt";

    /// <summary>
    /// Every buildable thing under a root, for the model to choose from. Depth
    /// limited because a deep tree of generated folders would otherwise take far
    /// longer to walk than the answer is worth.
    /// </summary>
    public static IReadOnlyList<BuildTarget> Find(string root, int maxDepth = 3, int limit = 40)
    {
        var found = new List<BuildTarget>();

        if (!Directory.Exists(root))
            return found;

        Walk(root, 0);
        return found;

        void Walk(string directory, int depth)
        {
            if (found.Count >= limit || depth > maxDepth)
                return;

            var target = Identify(directory);

            if (target != null)
            {
                found.Add(target);

                // A solution or Gradle root already covers what is beneath it,
                // so descending would only list its own projects again.
                return;
            }

            IEnumerable<string> children;

            try
            {
                children = Directory.EnumerateDirectories(directory);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                return;
            }

            foreach (var child in children)
            {
                if (SkippedDirectories.Contains(Path.GetFileName(child)))
                    continue;

                Walk(child, depth + 1);
            }
        }
    }

    public static bool HasGradleWrapper(string directory) =>
        File.Exists(Path.Combine(directory, GradleWrapperName));

    /// <summary>
    /// The wrapper script, which is how a Gradle project is meant to be built:
    /// it pins the Gradle version the project expects, so it works without
    /// Gradle being installed at all.
    /// </summary>
    public static string GradleWrapperName =>
        OperatingSystem.IsWindows() ? "gradlew.bat" : "gradlew";

    private static string? FirstMatch(string directory, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
