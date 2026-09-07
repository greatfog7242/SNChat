using Microsoft.Extensions.Logging.Abstractions;
using SNChat.Core.Models;
using SNChat.LLM.Interfaces;
using SNChat.LLM.Models;
using SNChat.LLM.Services;

namespace SNChat.Tests;

public class ConversationCompactorTests
{
    /// <summary>
    /// Returns whatever chunks it is given and keeps the request it was asked
    /// with, so the prompt the compactor builds can be inspected.
    /// </summary>
    private class StubProvider : ILLMProvider
    {
        private readonly StreamChunk[] _chunks;

        public StubProvider(params StreamChunk[] chunks) => _chunks = chunks;

        public StubProvider(string reply) : this(new StreamChunk { Content = reply, IsFinal = true }) { }

        public GenerateRequest? LastRequest { get; private set; }

        public string Name => "Stub";

        public async IAsyncEnumerable<StreamChunk> GenerateStreamAsync(GenerateRequest request)
        {
            LastRequest = request;

            foreach (var chunk in _chunks)
                yield return chunk;

            await Task.CompletedTask;
        }

        public Task<List<Model>> GetAvailableModelsAsync() => Task.FromResult(new List<Model>());
        public Task<string> GenerateAsync(GenerateRequest request) => Task.FromResult(string.Empty);
        public Task<bool> IsAvailableAsync() => Task.FromResult(true);
    }

    private class ThrowingProvider : ILLMProvider
    {
        public string Name => "Throwing";

        public async IAsyncEnumerable<StreamChunk> GenerateStreamAsync(GenerateRequest request)
        {
            await Task.CompletedTask;
            throw new HttpRequestException("the provider is down");
#pragma warning disable CS0162 // Unreachable, but the compiler needs a yield to see this as an iterator.
            yield break;
#pragma warning restore CS0162
        }

        public Task<List<Model>> GetAvailableModelsAsync() => Task.FromResult(new List<Model>());
        public Task<string> GenerateAsync(GenerateRequest request) => Task.FromResult(string.Empty);
        public Task<bool> IsAvailableAsync() => Task.FromResult(true);
    }

    private static ConversationCompactor Compactor() =>
        new(NullLogger<ConversationCompactor>.Instance);

    private static List<Message> Conversation(int count) =>
        Enumerable.Range(1, count)
            .Select(i => new Message
            {
                Role = i % 2 == 1 ? MessageRole.User : MessageRole.Assistant,
                Content = $"message {i}"
            })
            .ToList();

    [Fact]
    public void The_most_recent_messages_are_left_alone()
    {
        var live = Conversation(10);

        var folded = ConversationCompactor.Foldable(live, keepRecent: 4);

        Assert.Equal(6, folded.Count);
        Assert.Equal(live.Take(6), folded);
    }

    [Fact]
    public void Nothing_is_folded_while_the_conversation_is_shorter_than_what_is_kept()
    {
        Assert.Empty(ConversationCompactor.Foldable(Conversation(3), keepRecent: 6));
    }

    [Fact]
    public void A_single_message_is_not_worth_folding()
    {
        // A summary of one message is about as long as the message, so this
        // would spend a request to save nothing.
        Assert.Empty(ConversationCompactor.Foldable(Conversation(7), keepRecent: 6));
    }

    [Fact]
    public void Keeping_none_folds_everything()
    {
        var live = Conversation(4);

        Assert.Equal(live, ConversationCompactor.Foldable(live, keepRecent: 0));
    }

    [Fact]
    public async Task The_summary_comes_back_as_a_message_ready_to_stand_in_for_the_others()
    {
        var summary = await Compactor().SummarizeAsync(
            new StubProvider("  User wants a progress bar.  "), "some-model", Conversation(4));

        Assert.NotNull(summary);
        Assert.Equal("User wants a progress bar.", summary!.Content);
        Assert.Equal(MessageRole.System, summary.Role);
        Assert.True(summary.IsCompactionSummary);
        Assert.False(summary.IsCompacted);
    }

    [Fact]
    public async Task Progress_notices_are_not_mistaken_for_the_summary()
    {
        var provider = new StubProvider(
            new StreamChunk { Content = "Using web_search...", IsStatus = true },
            new StreamChunk { Content = "Notes.", IsFinal = true });

        var summary = await Compactor().SummarizeAsync(provider, "some-model", Conversation(4));

        Assert.Equal("Notes.", summary!.Content);
    }

    [Fact]
    public async Task The_messages_being_folded_go_in_as_one_labelled_transcript()
    {
        // Handed the real conversation, a model answers its last question
        // instead of summarizing anything - so the whole thing is quoted inside
        // a single message with the instruction after it.
        var provider = new StubProvider("Notes.");

        await Compactor().SummarizeAsync(provider, "some-model", Conversation(2));

        var sent = Assert.Single(provider.LastRequest!.Messages);
        Assert.Equal(MessageRole.User, sent.Role);
        Assert.Contains("[User]\nmessage 1", sent.Content);
        Assert.Contains("[Assistant]\nmessage 2", sent.Content);
        Assert.Contains("Summarize the conversation above", sent.Content);
    }

    [Fact]
    public async Task An_earlier_summary_is_folded_in_like_any_other_message()
    {
        // Otherwise summaries would stack up, one per compaction, and the
        // prompt would creep back up.
        var provider = new StubProvider("Notes.");

        var earlier = new Message
        {
            Role = MessageRole.System,
            Content = "what came before",
            IsCompactionSummary = true
        };

        await Compactor().SummarizeAsync(provider, "some-model",
            new[] { earlier, new Message { Role = MessageRole.User, Content = "and then" } });

        Assert.Contains("[Summary of earlier messages]\nwhat came before",
            provider.LastRequest!.Messages.Single().Content);
    }

    [Fact]
    public async Task No_tools_are_offered_for_a_summary()
    {
        var provider = new StubProvider("Notes.");

        await Compactor().SummarizeAsync(provider, "some-model", Conversation(4));

        Assert.Empty(provider.LastRequest!.Tools);
    }

    [Fact]
    public async Task Nothing_to_summarize_asks_the_model_nothing()
    {
        var provider = new StubProvider("Notes.");

        Assert.Null(await Compactor().SummarizeAsync(provider, "some-model", Array.Empty<Message>()));
        Assert.Null(provider.LastRequest);
    }

    [Fact]
    public async Task An_empty_answer_compacts_nothing_rather_than_replacing_history_with_blank()
    {
        var summary = await Compactor().SummarizeAsync(
            new StubProvider("   "), "some-model", Conversation(4));

        Assert.Null(summary);
    }

    [Fact]
    public async Task A_provider_that_fails_leaves_the_conversation_as_it_was()
    {
        // Not compacting is a worse prompt, not a broken one, so this must not
        // surface as an error in the middle of a conversation.
        var summary = await Compactor().SummarizeAsync(
            new ThrowingProvider(), "some-model", Conversation(4));

        Assert.Null(summary);
    }

    [Fact]
    public async Task Cancelling_a_compaction_is_not_swallowed_as_a_failed_summary()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Compactor().SummarizeAsync(
                new StubProvider("Notes."), "some-model", Conversation(4), cancelled.Token));
    }
}
