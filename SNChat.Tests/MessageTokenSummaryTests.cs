using SNChat.Core.Models;

namespace SNChat.Tests;

/// <summary>
/// What each card reports. The distinction that matters throughout: a figure
/// the provider does not report reads "n/a", never zero, because a reply with
/// no reasoning and a reply whose reasoning was never counted are different
/// things.
/// </summary>
public class MessageTokenSummaryTests
{
    [Fact]
    public void A_prompt_reports_only_its_input_count()
    {
        var message = new Message { Role = MessageRole.User, PromptTokens = 3659 };

        Assert.Equal("3,659 in", message.TokenSummary);
    }

    [Fact]
    public void A_reply_splits_thinking_from_response_and_shows_the_charge()
    {
        var message = new Message
        {
            Role = MessageRole.Assistant,
            CompletionTokens = 2000,
            ReasoningTokens = 1842,
            Cost = 0.0032m
        };

        Assert.Equal("2,000 out · 1,842 thinking + 158 response · $0.0032", message.TokenSummary);
    }

    /// <summary>Ollama reports one total and nothing else.</summary>
    [Fact]
    public void A_reply_from_a_provider_that_reports_neither_says_so()
    {
        var message = new Message { Role = MessageRole.Assistant, CompletionTokens = 431 };

        Assert.Equal("431 out · thinking n/a · cost n/a", message.TokenSummary);
    }

    /// <summary>
    /// Zero reasoning is a real measurement and must not be shown as "n/a".
    /// </summary>
    [Fact]
    public void Reported_zero_reasoning_is_not_the_same_as_unreported()
    {
        var message = new Message
        {
            Role = MessageRole.Assistant,
            CompletionTokens = 431,
            ReasoningTokens = 0
        };

        Assert.Contains("0 thinking + 431 response", message.TokenSummary);
        Assert.DoesNotContain("thinking n/a", message.TokenSummary);
    }

    [Fact]
    public void An_unmeasured_message_reports_nothing()
    {
        Assert.Equal(string.Empty, new Message().TokenSummary);
        Assert.False(new Message().HasTokenSummary);
    }
}
