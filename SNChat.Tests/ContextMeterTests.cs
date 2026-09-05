using SNChat.Core.Models;
using SNChat.Core.Services;
using SNChat.Core.Tools;

namespace SNChat.Tests;

public class ContextMeterTests
{
    private class FakeTool : ITool
    {
        public string Name { get; init; } = "search";
        public string Description { get; init; } = string.Empty;
        public ToolParameterSchema Parameters { get; init; } = new();

        public Task<string> ExecuteAsync(
            IReadOnlyDictionary<string, object?> arguments,
            CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);
    }

    private static Message Msg(string content, MessageRole role = MessageRole.User) =>
        new() { Role = role, Content = content };

    /// <summary>A message whose text alone estimates to exactly one token.</summary>
    private static Message OneTokenMessage() => Msg("abcd");

    [Fact]
    public void Text_is_estimated_at_four_characters_to_the_token()
    {
        Assert.Equal(3, TokenEstimator.Estimate("twelve chars"));
    }

    [Fact]
    public void A_part_used_token_still_counts_as_one()
    {
        // Rounding down would let a long run of short messages read as free.
        Assert.Equal(2, TokenEstimator.Estimate("abcde"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Nothing_costs_nothing(string? text)
    {
        Assert.Equal(0, TokenEstimator.Estimate(text));
    }

    [Fact]
    public void Each_message_costs_its_text_plus_the_framing_around_it()
    {
        var messages = new[] { OneTokenMessage(), OneTokenMessage() };

        Assert.Equal(2 * (1 + TokenEstimator.PerMessageOverhead), TokenEstimator.Estimate(messages));
    }

    [Fact]
    public void Compacted_messages_are_not_sent_but_the_summary_standing_for_them_is()
    {
        var folded = Msg("old", MessageRole.Assistant);
        folded.IsCompacted = true;

        var summary = new Message
        {
            Role = MessageRole.System,
            Content = "notes",
            IsCompactionSummary = true
        };

        var recent = Msg("new");

        var live = ContextMeter.LiveMessages(new[] { folded, summary, recent });

        Assert.Equal(new[] { summary, recent }, live);
    }

    [Fact]
    public void With_nothing_measured_yet_the_whole_prompt_is_estimated()
    {
        var messages = new[] { OneTokenMessage(), OneTokenMessage() };

        // "abcdefgh" is two tokens of system prompt; each message is one token
        // of text plus its framing.
        var usage = ContextMeter.Measure(messages, "abcdefgh", windowTokens: 100);

        Assert.Equal(2 + 2 * (1 + TokenEstimator.PerMessageOverhead), usage.UsedTokens);
    }

    [Fact]
    public void A_measured_prompt_replaces_the_estimate_for_everything_it_covered()
    {
        var messages = new[] { Msg("a very long first message that would estimate high"), OneTokenMessage() };

        var usage = ContextMeter.Measure(
            messages, "a system prompt", windowTokens: 1000,
            measuredPromptTokens: 500, measuredThrough: 1);

        // 500 for the first message, the system prompt and anything else the
        // provider counted; only the second message is still a guess.
        Assert.Equal(500 + 1 + TokenEstimator.PerMessageOverhead, usage.UsedTokens);
    }

    [Fact]
    public void A_measurement_covering_every_message_leaves_nothing_to_estimate()
    {
        var messages = new[] { OneTokenMessage(), OneTokenMessage() };

        var usage = ContextMeter.Measure(
            messages, "ignored once measured", windowTokens: 1000,
            measuredPromptTokens: 500, measuredThrough: 2);

        Assert.Equal(500, usage.UsedTokens);
    }

    [Fact]
    public void A_measurement_reaching_past_the_end_is_discarded()
    {
        // What compaction leaves behind: the count described messages that are
        // no longer in the list, so it describes nothing.
        var messages = new[] { OneTokenMessage() };

        var usage = ContextMeter.Measure(
            messages, systemPrompt: null, windowTokens: 1000,
            measuredPromptTokens: 900, measuredThrough: 5);

        Assert.Equal(1 + TokenEstimator.PerMessageOverhead, usage.UsedTokens);
    }

    [Fact]
    public void Tool_definitions_are_counted_because_they_dominate_the_prompt()
    {
        // Measured in the real app: nineteen MCP tools cost about sixteen
        // thousand tokens against a hundred and thirty for the message that
        // prompted them. Leaving them out held the meter near zero until the
        // provider's first count arrived, which then sent it straight to full.
        var tools = new[]
        {
            new FakeTool
            {
                Name = "web_search",
                Description = new string('d', 400),
                Parameters = new ToolParameterSchema
                {
                    Properties = { ["query"] = new ToolParameterProperty { Description = new string('p', 400) } }
                }
            }
        };

        var toolTokens = TokenEstimator.EstimateTools(tools);

        // Both descriptions alone are 800 characters, so this must be well over
        // a hundred tokens rather than a token or two of tool name.
        Assert.True(toolTokens > 200, $"expected the schema to be counted, got {toolTokens}");

        var messages = new[] { OneTokenMessage() };

        var withTools = ContextMeter.Measure(messages, null, 100_000, toolTokens: toolTokens);
        var without = ContextMeter.Measure(messages, null, 100_000);

        Assert.Equal(without.UsedTokens + toolTokens, withTools.UsedTokens);
    }

    [Fact]
    public void Tools_are_not_added_on_top_of_a_measured_prompt_that_already_carried_them()
    {
        // The provider counted the whole prompt, tool definitions included, so
        // adding them again would double-count the largest term there is.
        var messages = new[] { OneTokenMessage() };

        var usage = ContextMeter.Measure(
            messages, "system", windowTokens: 100_000,
            measuredPromptTokens: 16386, measuredThrough: 1, toolTokens: 16_000);

        Assert.Equal(16386, usage.UsedTokens);
    }

    [Fact]
    public void A_tool_schema_that_will_not_serialize_does_not_stop_the_rest_being_counted()
    {
        var tools = new[]
        {
            new FakeTool { Name = "first", Description = new string('d', 200) },
            new FakeTool { Name = "second", Description = new string('d', 200) }
        };

        Assert.True(TokenEstimator.EstimateTools(tools) > 100);
    }

    [Fact]
    public void No_tools_cost_nothing()
    {
        Assert.Equal(0, TokenEstimator.EstimateTools(Array.Empty<ITool>()));
    }

    [Fact]
    public void An_unknown_window_reports_no_usage_rather_than_a_made_up_figure()
    {
        var usage = new ContextUsage(UsedTokens: 500, WindowTokens: 0);

        Assert.False(usage.IsKnown);
        Assert.Equal(0, usage.Percent);
        Assert.False(usage.HasReached(1));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(40, 50)]
    [InlineData(80, 100)]
    public void Usage_reads_as_a_percentage_of_the_window(int used, int expectedPercent)
    {
        Assert.Equal(expectedPercent, new ContextUsage(used, WindowTokens: 80).Percent);
    }

    [Fact]
    public void A_prompt_larger_than_the_window_reads_as_full_rather_than_more_than_full()
    {
        // The request is about to be rejected, but there is nothing past full
        // for a bar to show.
        var usage = new ContextUsage(UsedTokens: 300, WindowTokens: 100);

        Assert.Equal(100, usage.Percent);
        Assert.Equal(1.0, usage.Fraction);
    }

    [Fact]
    public void The_threshold_is_reached_on_the_percentage_itself_not_only_past_it()
    {
        var usage = new ContextUsage(UsedTokens: 80, WindowTokens: 100);

        Assert.True(usage.HasReached(80));
        Assert.False(usage.HasReached(81));
    }
}
