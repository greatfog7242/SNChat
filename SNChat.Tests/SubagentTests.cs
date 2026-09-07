using Microsoft.Extensions.Logging.Abstractions;
using SNChat.Core.Models;
using SNChat.Core.Services;
using SNChat.Core.Tools;
using SNChat.LLM.Interfaces;
using SNChat.LLM.Models;
using SNChat.LLM.Tools;

namespace SNChat.Tests;

/// <summary>
/// Delegation: an agent definition round-tripping through a file, and the tool
/// that runs one.
///
/// The tool set a subagent is given is the part worth testing hardest. It is the
/// permission boundary, and two of its rules exist because breaking them is
/// silent rather than loud - a subagent that can delegate recurses, and one that
/// can signal completion stops its parent's run instead of its own.
/// </summary>
public class SubagentTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "snchat-agents-" + Guid.NewGuid().ToString("N")[..8]);

    public SubagentTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private AgentDefinitionService Service() =>
        new(NullLogger<AgentDefinitionService>.Instance, _folder);

    // --- Definitions on disk ---------------------------------------------

    [Fact]
    public void An_agent_survives_being_written_and_read_back()
    {
        var service = Service();

        service.Save(new AgentDefinition
        {
            Name = "explorer",
            Description = "Looks things up",
            AllowedTools = new List<string> { "read_file", "search_files" },
            Model = "qwen3:8b",
            MaxToolIterations = 25,
            SystemPrompt = "You investigate and report."
        });

        var loaded = Assert.Single(service.LoadAll());

        Assert.Equal("explorer", loaded.Name);
        Assert.Equal("Looks things up", loaded.Description);
        Assert.Equal(new[] { "read_file", "search_files" }, loaded.AllowedTools);
        Assert.Equal("qwen3:8b", loaded.Model);
        Assert.Equal(25, loaded.MaxToolIterations);
        Assert.Equal("You investigate and report.", loaded.SystemPrompt);
    }

    [Fact]
    public void A_prompt_containing_a_horizontal_rule_survives()
    {
        // The bug that lost six conversations. A system prompt is exactly the
        // kind of long text that ends up with a "---" line in it, and YAML
        // writes it as an indented block scalar, so both the naive substring
        // split and the trim-both-ends fix would truncate this.
        var service = Service();

        var prompt = "Do the work.\n\n---\n\nThen report what you found.";

        service.Save(new AgentDefinition { Name = "ruled", SystemPrompt = prompt });

        Assert.Equal(prompt, Assert.Single(service.LoadAll()).SystemPrompt);
    }

    [Fact]
    public void A_single_tool_name_where_a_list_belongs_is_understood()
    {
        // A natural hand-editing mistake, and treating it as "no tools" would
        // leave the agent unable to work with no obvious cause.
        File.WriteAllText(Path.Combine(_folder, "hand-written.md"),
            "---\nname: reader\nallowed_tools: read_file\n---\n\nRead things.");

        Assert.Equal(new[] { "read_file" }, Assert.Single(Service().LoadAll()).AllowedTools);
    }

    [Fact]
    public void One_unreadable_file_does_not_cost_the_others()
    {
        var service = Service();
        service.Save(new AgentDefinition { Name = "good", SystemPrompt = "Fine." });

        File.WriteAllText(Path.Combine(_folder, "broken.md"), "no frontmatter here");

        Assert.Equal("good", Assert.Single(service.LoadAll()).Name);
    }

    [Fact]
    public void The_defaults_are_written_once_and_stay_deleted()
    {
        // Otherwise removing the agents you do not want would be undone at the
        // next launch, and there would be no way to turn the feature off.
        var service = Service();

        service.SeedDefaultsOnFirstRun();
        Assert.NotEmpty(service.LoadAll());

        foreach (var file in Directory.GetFiles(_folder, "*.md"))
            File.Delete(file);

        service.SeedDefaultsOnFirstRun();

        Assert.Empty(service.LoadAll());
    }

    [Fact]
    public void The_default_agents_ask_only_for_tools_that_could_exist()
    {
        // A default naming a tool that does not exist would ship an agent that
        // quietly cannot do its job.
        var service = Service();
        service.SeedDefaultsOnFirstRun();

        foreach (var agent in service.LoadAll())
        {
            Assert.NotEmpty(agent.AllowedTools);

            Assert.DoesNotContain(agent.AllowedTools,
                name => RunSubagentTool.NeverDelegated.Contains(name, StringComparer.OrdinalIgnoreCase));
        }
    }

    // --- Which tools a subagent gets --------------------------------------

    private class FakeTool : ITool
    {
        public FakeTool(string name) => Name = name;

        public string Name { get; }
        public string Description => Name;
        public ToolParameterSchema Parameters => new();

        public Task<string> ExecuteAsync(
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);
    }

    private static IToolRegistry RegistryWith(params string[] names)
    {
        var registry = new ToolRegistry(NullLogger<ToolRegistry>.Instance);

        foreach (var name in names)
            registry.Register(new FakeTool(name));

        return registry;
    }

    private RunSubagentTool Tool(
        IToolRegistry registry,
        ActiveModel? activeModel = null,
        ILLMProvider? provider = null)
    {
        return new RunSubagentTool(
            Service(),
            () => new StubFactory(provider ?? new StubProvider("done")),
            () => registry,
            activeModel ?? new ActiveModel { ProviderName = "Stub", Model = "test-model" },
            NullLogger<RunSubagentTool>.Instance);
    }

    [Fact]
    public void An_agent_gets_only_the_tools_it_asked_for()
    {
        var tool = Tool(RegistryWith("read_file", "build_project", "git_commit"));

        var granted = tool.ToolsFor(new AgentDefinition
        {
            Name = "reader",
            AllowedTools = new List<string> { "read_file" }
        });

        Assert.Equal(new[] { "read_file" }, granted.Select(t => t.Name));
    }

    [Fact]
    public void Asking_for_nothing_in_particular_means_everything_delegable()
    {
        var tool = Tool(RegistryWith("read_file", "build_project"));

        var granted = tool.ToolsFor(new AgentDefinition { Name = "generalist" });

        Assert.Equal(2, granted.Count);
    }

    [Fact]
    public void A_subagent_can_never_delegate_further()
    {
        // Nothing downstream would stop a subagent that can call this from
        // calling it on itself.
        var tool = Tool(RegistryWith("read_file", "run_subagent"));

        foreach (var agent in new[]
                 {
                     new AgentDefinition { Name = "greedy", AllowedTools = new List<string> { "run_subagent" } },
                     new AgentDefinition { Name = "unrestricted" }
                 })
        {
            Assert.DoesNotContain(tool.ToolsFor(agent), t => t.Name == "run_subagent");
        }
    }

    [Fact]
    public void A_subagent_can_never_signal_the_run_complete()
    {
        // The subtle one. AgentSignals is a single shared object, so a subagent
        // calling task_complete would not be ending its own work - it would be
        // telling the parent's autonomous loop that the whole job was finished.
        // A delegated search would end the run it was helping with.
        var tool = Tool(RegistryWith("read_file", "task_complete"));

        foreach (var agent in new[]
                 {
                     new AgentDefinition { Name = "eager", AllowedTools = new List<string> { "task_complete" } },
                     new AgentDefinition { Name = "unrestricted" }
                 })
        {
            Assert.DoesNotContain(tool.ToolsFor(agent), t => t.Name == "task_complete");
        }
    }

    // --- Running one -------------------------------------------------------

    private class StubFactory : ILLMProviderFactory
    {
        private readonly ILLMProvider _provider;

        public StubFactory(ILLMProvider provider) => _provider = provider;

        public ILLMProvider GetProvider(string providerName) =>
            providerName == _provider.Name
                ? _provider
                : throw new ArgumentException($"no provider named {providerName}");

        public IEnumerable<string> GetAvailableProviders() => new[] { _provider.Name };

        public void RegisterProvider(string name, ILLMProvider provider) =>
            throw new NotSupportedException();
    }

    private class StubProvider : ILLMProvider
    {
        private readonly string _reply;

        public StubProvider(string reply) => _reply = reply;

        public GenerateRequest? LastRequest { get; private set; }

        public string Name => "Stub";

        public async IAsyncEnumerable<StreamChunk> GenerateStreamAsync(GenerateRequest request)
        {
            LastRequest = request;

            // A status chunk alongside the answer, because the real providers
            // emit them and they must not reach the parent as content.
            yield return new StreamChunk { Content = "thinking...", IsStatus = true };
            yield return new StreamChunk { Content = _reply, IsFinal = true };

            await Task.CompletedTask;
        }

        public Task<List<Model>> GetAvailableModelsAsync() => Task.FromResult(new List<Model>());
        public Task<string> GenerateAsync(GenerateRequest request) => Task.FromResult(string.Empty);
        public Task<bool> IsAvailableAsync() => Task.FromResult(true);
    }

    private static Task<string> Run(ITool tool, string agent, string task) =>
        tool.ExecuteAsync(new Dictionary<string, object?> { ["agent"] = agent, ["task"] = task });

    private void GiveAgent(string name, params string[] allowed)
    {
        Service().Save(new AgentDefinition
        {
            Name = name,
            AllowedTools = allowed.ToList(),
            SystemPrompt = "Do the thing."
        });
    }

    [Fact]
    public async Task What_the_subagent_concluded_comes_back()
    {
        GiveAgent("explorer", "read_file");

        var provider = new StubProvider("The parser lives in MarkdownDocument.cs.");
        var report = await Run(Tool(RegistryWith("read_file"), provider: provider), "explorer", "Find the parser");

        Assert.Contains("MarkdownDocument.cs", report);
        Assert.Contains("explorer", report);

        // The progress notice is the subagent's own business.
        Assert.DoesNotContain("thinking...", report);
    }

    [Fact]
    public async Task The_task_and_the_agents_own_instruction_are_what_it_runs_on()
    {
        // It starts from nothing but these: it cannot see the parent's
        // conversation, which is the whole point.
        GiveAgent("explorer", "read_file");

        var provider = new StubProvider("done");
        await Run(Tool(RegistryWith("read_file"), provider: provider), "explorer", "Find the parser");

        var request = provider.LastRequest!;

        Assert.Equal("Do the thing.", request.SystemPrompt);
        Assert.Equal("Find the parser", Assert.Single(request.Messages).Content);
        Assert.Equal("read_file", Assert.Single(request.Tools).Name);
    }

    [Fact]
    public async Task An_agents_own_model_overrides_the_conversations()
    {
        Service().Save(new AgentDefinition
        {
            Name = "cheap",
            Model = "small-local-model",
            SystemPrompt = "Be brief."
        });

        var provider = new StubProvider("done");

        await Run(
            Tool(RegistryWith("read_file"),
                new ActiveModel { ProviderName = "Stub", Model = "expensive-model" },
                provider),
            "cheap", "Something small");

        Assert.Equal("small-local-model", provider.LastRequest!.Model);
    }

    [Fact]
    public async Task Without_its_own_model_it_runs_on_the_conversations()
    {
        GiveAgent("explorer", "read_file");

        var provider = new StubProvider("done");

        await Run(
            Tool(RegistryWith("read_file"),
                new ActiveModel { ProviderName = "Stub", Model = "the-current-one" },
                provider),
            "explorer", "Something");

        Assert.Equal("the-current-one", provider.LastRequest!.Model);
    }

    [Fact]
    public async Task An_agent_that_does_not_exist_is_reported_with_the_ones_that_do()
    {
        GiveAgent("explorer", "read_file");

        var report = await Run(Tool(RegistryWith("read_file")), "reviewer", "Do something");

        Assert.Contains("no subagent named 'reviewer'", report, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("explorer", report);
    }

    [Fact]
    public async Task A_task_with_nothing_in_it_is_refused_before_anything_runs()
    {
        GiveAgent("explorer", "read_file");

        var provider = new StubProvider("done");

        Assert.Contains("needs a task",
            await Run(Tool(RegistryWith("read_file"), provider: provider), "explorer", "   "));

        Assert.Null(provider.LastRequest);
    }

    [Fact]
    public async Task With_no_model_selected_it_says_so_rather_than_failing_obscurely()
    {
        GiveAgent("explorer", "read_file");

        var report = await Run(
            Tool(RegistryWith("read_file"), new ActiveModel()),
            "explorer", "Do something");

        Assert.Contains("No model is selected", report);
    }

    [Fact]
    public async Task A_subagent_that_says_nothing_is_reported_rather_than_returning_blank()
    {
        // An empty tool result reads to the model as a tool that worked and
        // found nothing, which is a different and misleading thing.
        GiveAgent("explorer", "read_file");

        var report = await Run(
            Tool(RegistryWith("read_file"), provider: new StubProvider("   ")),
            "explorer", "Do something");

        Assert.Contains("without reporting anything", report);
    }

    [Fact]
    public async Task A_runaway_report_is_cut_off_and_says_that_it_was()
    {
        // The whole purpose is to spend less of the parent's context than doing
        // the work inline would. A subagent pasting a file back would undo it.
        GiveAgent("explorer", "read_file");

        var huge = new string('x', RunSubagentTool.MaxReportCharacters * 2);

        var report = await Run(
            Tool(RegistryWith("read_file"), provider: new StubProvider(huge)),
            "explorer", "Do something");

        Assert.True(report.Length < huge.Length);
        Assert.Contains("cut off", report);
    }

    [Fact]
    public async Task A_provider_that_fails_costs_the_subagent_and_not_the_turn()
    {
        GiveAgent("explorer", "read_file");

        var report = await Run(
            Tool(RegistryWith("read_file"), provider: new BrokenProvider()),
            "explorer", "Do something");

        Assert.Contains("failed", report);
        Assert.Contains("the provider is down", report);
    }

    private class BrokenProvider : ILLMProvider
    {
        public string Name => "Stub";

        public async IAsyncEnumerable<StreamChunk> GenerateStreamAsync(GenerateRequest request)
        {
            await Task.CompletedTask;
            throw new HttpRequestException("the provider is down");
#pragma warning disable CS0162 // Unreachable, but the compiler needs a yield to see an iterator.
            yield break;
#pragma warning restore CS0162
        }

        public Task<List<Model>> GetAvailableModelsAsync() => Task.FromResult(new List<Model>());
        public Task<string> GenerateAsync(GenerateRequest request) => Task.FromResult(string.Empty);
        public Task<bool> IsAvailableAsync() => Task.FromResult(true);
    }

    [Fact]
    public async Task An_agent_whose_tools_are_all_missing_says_so_instead_of_guessing()
    {
        // The likely real cause is a definition naming MCP tools from a server
        // that is not running. Handed no tools, a model will still answer -
        // confidently, and from nothing.
        GiveAgent("explorer", "read_text_file", "directory_tree");

        var provider = new StubProvider("The parser is in Foo.cs.");

        var report = await Run(
            Tool(RegistryWith("build_project"), provider: provider),
            "explorer", "Find the parser");

        Assert.Contains("none of which are available", report);
        Assert.Null(provider.LastRequest);
    }

    [Fact]
    public async Task An_agent_written_during_the_session_can_be_used_without_a_restart()
    {
        var tool = Tool(RegistryWith("read_file"));

        // Constructed before the agent existed.
        GiveAgent("latecomer", "read_file");

        Assert.DoesNotContain("no subagent named", await Run(tool, "latecomer", "Do something"),
            StringComparison.OrdinalIgnoreCase);
    }
}
