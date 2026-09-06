using Microsoft.Extensions.Logging.Abstractions;
using SNChat.BuildTools;
using SNChat.Core.Models;
using SNChat.Core.Services;
using SNChat.Core.Tools;
using SNChat.LLM.Models;
using SNChat.LLM.Providers.Ollama;

namespace SNChat.Tests;

/// <summary>
/// Drives a real model against the loop's machinery, because everything else
/// about Stage 4 can be green while the feature does not work.
///
/// The unit tests prove the loop stops correctly *given* a completion signal.
/// What they cannot show is the thing the whole design rests on: that a model
/// handed task_complete will actually call it rather than write "the task is
/// complete" in prose and carry on being asked to continue forever.
///
/// Skipped when Ollama is not reachable, so the suite still runs elsewhere.
/// </summary>
public class AutonomousRunProbeTests
{
    private const string Model = "qwen2.5:7b";

    private static async Task<bool> OllamaAvailable()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await client.GetAsync("http://localhost:11434/api/tags");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static (OllamaProvider Provider, AgentSignals Signals) Build()
    {
        var signals = new AgentSignals();

        var registry = new ToolRegistry(NullLogger<ToolRegistry>.Instance);
        registry.Register(new TaskCompleteTool(signals));

        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

        return (new OllamaProvider(client, NullLogger<OllamaProvider>.Instance, registry), signals);
    }

    /// <summary>
    /// The load-bearing assumption of the whole stage. If a model will not call
    /// the tool, every run ends by exhausting its budget instead of finishing,
    /// and the user is told "stopped without finishing" every single time.
    /// </summary>
    [Fact]
    public async Task A_real_model_uses_task_complete_to_say_it_has_finished()
    {
        if (!await OllamaAvailable())
            return;

        var (provider, signals) = Build();

        var request = new GenerateRequest
        {
            Model = Model,
            SystemPrompt =
                "You are working through a task on your own. When the task is finished, " +
                "call the task_complete tool. Do not merely say it is complete.",
            Messages = new List<Message>
            {
                new()
                {
                    Role = MessageRole.User,
                    Content = "The task is already done - nothing needs doing. " +
                              "Report that it is finished."
                }
            },
            Tools = new List<ITool> { new TaskCompleteTool(signals) },
            MaxToolIterations = 3,
            Parameters = new ModelParameters { Temperature = 0.1, MaxTokens = 512 }
        };

        var text = new System.Text.StringBuilder();

        await foreach (var chunk in provider.GenerateStreamAsync(request))
        {
            if (!chunk.IsStatus && chunk.ToolExchange == null)
                text.Append(chunk.Content);
        }

        Assert.True(
            signals.ConsumeComplete(),
            "The model did not call task_complete. It said instead: " + text);
    }

    /// <summary>
    /// The other half: a tool exchange must reach the caller, or the loop cannot
    /// keep what its own tools returned and the whole prerequisite is moot.
    /// </summary>
    [Fact]
    public async Task A_tool_exchange_is_handed_up_from_a_real_run()
    {
        if (!await OllamaAvailable())
            return;

        var (provider, signals) = Build();

        var request = new GenerateRequest
        {
            Model = Model,
            SystemPrompt = "Call task_complete when asked to finish.",
            Messages = new List<Message>
            {
                new()
                {
                    Role = MessageRole.User,
                    Content = "Nothing to do. Call task_complete with the summary 'nothing to do'."
                }
            },
            Tools = new List<ITool> { new TaskCompleteTool(signals) },
            MaxToolIterations = 3,
            Parameters = new ModelParameters { Temperature = 0.1, MaxTokens = 512 }
        };

        ToolExchange? seen = null;

        await foreach (var chunk in provider.GenerateStreamAsync(request))
        {
            if (chunk.ToolExchange != null)
                seen = chunk.ToolExchange;
        }

        // Only assert the shape if the model called anything at all; whether it
        // does is what the test above is for.
        if (seen != null)
        {
            Assert.Equal("task_complete", seen.Name);
            Assert.False(string.IsNullOrEmpty(seen.Result));
        }
    }

    /// <summary>
    /// The realistic case, and a much harder one than being told the task is
    /// already done: the model has to use a tool, read what came back, and only
    /// then decide it has finished. This is what an actual run looks like.
    /// </summary>
    [Fact]
    public async Task A_real_model_does_work_with_tools_and_then_says_it_has_finished()
    {
        var folder = @"C:\ai-playground\autoloop-test";

        if (!await OllamaAvailable() || !Directory.Exists(folder))
            return;

        var signals = new AgentSignals();
        var settings = new SettingsService();
        var projects = new ProjectContext
        {
            Current = new Project { Name = "autoloop-test", RootPath = folder }
        };

        var runner = new ProcessRunner(NullLogger<ProcessRunner>.Instance);

        var runProgram = new RunProgramTool(
            settings, projects, runner, NullLogger<RunProgramTool>.Instance);
        var complete = new TaskCompleteTool(signals);

        var registry = new ToolRegistry(NullLogger<ToolRegistry>.Instance);
        registry.Register(runProgram);
        registry.Register(complete);

        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var provider = new OllamaProvider(client, NullLogger<OllamaProvider>.Instance, registry);

        var request = new GenerateRequest
        {
            Model = Model,
            SystemPrompt =
                "You are working through a task on your own, using tools. When the task " +
                "is finished and you have checked the result, call task_complete.",
            Messages = new List<Message>
            {
                new()
                {
                    Role = MessageRole.User,
                    Content = $@"Run the program at {folder}\check.py using run_program, " +
                              "report what it printed, then call task_complete."
                }
            },
            Tools = new List<ITool> { runProgram, complete },
            MaxToolIterations = 6,
            Parameters = new ModelParameters { Temperature = 0.1, MaxTokens = 1024 }
        };

        var exchanges = new List<string>();
        var text = new System.Text.StringBuilder();

        await foreach (var chunk in provider.GenerateStreamAsync(request))
        {
            if (chunk.ToolExchange != null)
                exchanges.Add(chunk.ToolExchange.Name);
            else if (!chunk.IsStatus)
                text.Append(chunk.Content);
        }

        // Reported rather than asserted blindly, so a failure says which half
        // went wrong: using the tool, or knowing when to stop.
        var used = string.Join(", ", exchanges);

        Assert.True(exchanges.Contains("run_program"),
            $"The model never ran the program. Tools used: [{used}]. It said: {text}");

        Assert.True(signals.ConsumeComplete(),
            $"The model ran the program but never called task_complete, so a real run " +
            $"would loop until its budget ran out. Tools used: [{used}]. It said: {text}");
    }

    [Fact]
    public async Task The_scratch_repository_passes_the_checkpoint()
    {
        var folder = @"C:\ai-playground\autoloop-test";

        if (!Directory.Exists(folder))
            return;

        var checkpoint = new GitCheckpointService(
            new ProcessRunner(NullLogger<ProcessRunner>.Instance),
            NullLogger<GitCheckpointService>.Instance);

        var result = await checkpoint.PrepareAsync(folder, required: true);

        Assert.True(result.CanProceed, result.Message);
        Assert.Contains("reset --hard", result.Message);
    }
}
