using SNChat.Core.Models;
using SNChat.Core.Services;

namespace SNChat.Tests;

/// <summary>
/// The stored header carries everything about a message except its text. It
/// gained fields after conversations had already been written, so the format
/// has to stay readable backwards - a file saved before any of this existed
/// must still load, just without the extra detail.
/// </summary>
public class MessageHeaderTests
{
    /// <summary>Exactly the shape written before per-message detail was stored.</summary>
    private const string LegacyHeader = "3 (Assistant) - 2026-08-31 17:19:36";

    [Fact]
    public void A_header_written_before_this_existed_still_parses()
    {
        Assert.True(MessageHeader.TryParse(LegacyHeader, out var role, out var timestamp, out var facts));

        Assert.Equal(MessageRole.Assistant, role);
        Assert.Equal(new DateTime(2026, 8, 31, 17, 19, 36, DateTimeKind.Utc), timestamp);
        Assert.Null(facts.CompletionTokens);
        Assert.Equal(string.Empty, facts.ModelName);
    }

    /// <summary>
    /// The timestamp used to be read as "everything after the dash", which the
    /// metadata appended after it would have swallowed whole.
    /// </summary>
    [Fact]
    public void Metadata_after_the_timestamp_does_not_corrupt_it()
    {
        const string header =
            "3 (Assistant) - 2026-08-31 17:19:36 [provider=Ollama; model=qwen2.5:7b; out=2000]";

        Assert.True(MessageHeader.TryParse(header, out _, out var timestamp, out var facts));

        Assert.Equal(new DateTime(2026, 8, 31, 17, 19, 36, DateTimeKind.Utc), timestamp);
        Assert.Equal("Ollama", facts.Provider);
        Assert.Equal("qwen2.5:7b", facts.ModelName);
        Assert.Equal(2000, facts.CompletionTokens);
    }

    [Fact]
    public void A_reply_round_trips_everything_it_was_measured_with()
    {
        var original = new Message
        {
            Role = MessageRole.Assistant,
            Timestamp = new DateTime(2026, 8, 31, 17, 19, 36, DateTimeKind.Utc),
            Provider = "OpenRouter",
            ModelName = "google/gemini-3.7-flash",
            CompletionTokens = 2000,
            ReasoningTokens = 1842,
            Cost = 0.0032m
        };

        var line = MessageHeader.Format(3, original);
        var header = line["## Message ".Length..];

        Assert.True(MessageHeader.TryParse(header, out var role, out var timestamp, out var facts));

        Assert.Equal(original.Role, role);
        Assert.Equal(original.Timestamp, timestamp);
        Assert.Equal(original.Provider, facts.Provider);
        Assert.Equal(original.ModelName, facts.ModelName);
        Assert.Equal(original.CompletionTokens, facts.CompletionTokens);
        Assert.Equal(original.ReasoningTokens, facts.ReasoningTokens);
        Assert.Equal(original.Cost, facts.Cost);
    }

    [Fact]
    public void A_prompt_round_trips_its_input_count()
    {
        var original = new Message
        {
            Role = MessageRole.User,
            Timestamp = new DateTime(2026, 8, 31, 17, 19, 36, DateTimeKind.Utc),
            PromptTokens = 3659
        };

        var header = MessageHeader.Format(1, original)["## Message ".Length..];

        Assert.True(MessageHeader.TryParse(header, out var role, out _, out var facts));
        Assert.Equal(MessageRole.User, role);
        Assert.Equal(3659, facts.PromptTokens);
        Assert.Null(facts.CompletionTokens);
    }

    /// <summary>
    /// An unmeasured message must not gain an empty bracket, both to keep old
    /// and new files identical where nothing was measured, and because a
    /// stray "[]" would be visible in a file people read by hand.
    /// </summary>
    [Fact]
    public void Nothing_measured_writes_no_metadata_at_all()
    {
        var message = new Message
        {
            Role = MessageRole.User,
            Timestamp = new DateTime(2026, 8, 31, 17, 19, 36, DateTimeKind.Utc)
        };

        Assert.Equal("## Message 1 (User) - 2026-08-31 17:19:36", MessageHeader.Format(1, message));
    }

    /// <summary>Zero is a measurement and has to survive; null must not become zero.</summary>
    [Fact]
    public void Reported_zero_survives_the_round_trip()
    {
        var message = new Message
        {
            Role = MessageRole.Assistant,
            CompletionTokens = 431,
            ReasoningTokens = 0
        };

        var header = MessageHeader.Format(1, message)["## Message ".Length..];

        Assert.True(MessageHeader.TryParse(header, out _, out _, out var facts));
        Assert.Equal(0, facts.ReasoningTokens);
    }

    /// <summary>Model ids carry slashes, colons and dots; none may break parsing.</summary>
    [Fact]
    public void Awkward_model_ids_survive()
    {
        var message = new Message
        {
            Role = MessageRole.Assistant,
            Provider = "Ollama",
            ModelName = "orcarouter/Qwen3.8-27B-Uncensored:latest"
        };

        var header = MessageHeader.Format(1, message)["## Message ".Length..];

        Assert.True(MessageHeader.TryParse(header, out _, out _, out var facts));
        Assert.Equal("orcarouter/Qwen3.8-27B-Uncensored:latest", facts.ModelName);
    }

    [Fact]
    public void A_header_with_no_role_is_rejected()
    {
        Assert.False(MessageHeader.TryParse("3 - 2026-08-31 17:19:36", out _, out _, out _));
    }
}
