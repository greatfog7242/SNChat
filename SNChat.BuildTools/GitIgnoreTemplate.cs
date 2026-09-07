using System.Text;

namespace SNChat.BuildTools;

/// <summary>
/// The ignore file written when the app creates a repository for a project.
///
/// Exists because the alternative is worse than untidy. Creating a repository
/// commits everything already in the folder, and a folder that has been built in
/// holds an object directory, a node_modules or a virtual environment - tens of
/// thousands of files that make the first commit enormous, slow every later
/// status check, and bury the actual code in the history.
///
/// One list for every language rather than one per project kind. Ignoring
/// node_modules in a C++ project costs nothing and is never wrong, while
/// detecting the kind first would mean a polyglot repository silently getting
/// half a list. The comments matter as much as the patterns: this file lands in
/// somebody's project, and they should be able to see why each line is there and
/// delete the ones they disagree with.
/// </summary>
public static class GitIgnoreTemplate
{
    public const string FileName = ".gitignore";

    /// <summary>
    /// Deliberately conservative. Everything here is either generated, fetched,
    /// or belongs to one machine - nothing that a person wrote by hand.
    /// </summary>
    public static string Contents { get; } = Build();

    /// <summary>
    /// Writes the file unless the folder already has one, and reports whether it
    /// did.
    ///
    /// An existing .gitignore is never touched, not even to append. It is the
    /// project's own decision about what belongs in it, and quietly editing a
    /// file somebody wrote is a poor way to repay them for having written it.
    /// </summary>
    public static bool WriteIfAbsent(string folder)
    {
        var path = Path.Combine(folder, FileName);

        if (File.Exists(path))
            return false;

        File.WriteAllText(path, Contents);

        return true;
    }

    private static string Build()
    {
        var text = new StringBuilder();

        void Section(string heading, params string[] patterns)
        {
            text.Append("# ").Append(heading).Append('\n');

            foreach (var pattern in patterns)
                text.Append(pattern).Append('\n');

            text.Append('\n');
        }

        text.Append(
            "# Written by SNChat when it created this repository, so that build output,\n" +
            "# downloaded packages and editor files stay out of your history.\n" +
            "#\n" +
            "# It is yours now - edit or delete anything here. SNChat will not write this\n" +
            "# file again, and never changes one that already exists.\n\n");

        Section("Build output",
            "bin/", "obj/", "build/", "dist/", "out/", "target/", "*.o", "*.obj", "*.exe",
            "*.dll", "*.so", "*.dylib", "*.pdb", "*.class");

        Section("CMake",
            "CMakeCache.txt", "CMakeFiles/", "cmake-build-*/", "compile_commands.json");

        Section("Python",
            "__pycache__/", "*.py[cod]", ".venv/", "venv/", "env/",
            "*.egg-info/", ".pytest_cache/", ".mypy_cache/");

        Section("Node and TypeScript",
            "node_modules/", "npm-debug.log*", "yarn-error.log*", ".next/", ".nuxt/");

        Section("Java, Kotlin and Android",
            ".gradle/", "local.properties", "*.apk", "*.aab", "*.jar", ".cxx/");

        Section("Ruby",
            ".bundle/", "vendor/bundle/", "*.gem");

        Section("Editors and IDEs",
            ".vs/", ".vscode/", ".idea/", "*.user", "*.suo", "*.swp", "*~");

        Section("Operating system",
            ".DS_Store", "Thumbs.db", "desktop.ini");

        Section("Logs and scratch files",
            "*.log", "*.tmp", "*.bak");

        // Last, and worth the separate heading: this is the one group where
        // getting it wrong is not merely untidy.
        Section("Secrets - never commit these",
            ".env", ".env.*", "*.pem", "*.key", "secrets.json");

        return text.ToString().TrimEnd() + "\n";
    }
}
