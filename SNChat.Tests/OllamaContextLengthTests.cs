using System.Text.Json;
using SNChat.LLM.Providers.Ollama;

namespace SNChat.Tests;

/// <summary>
/// The model list carries no context length, so it is read from /api/show. It
/// used to be hardcoded to 4096 for every model, which on a 256k model put the
/// context meter at 100% on the first message and compacted a conversation that
/// had barely started.
/// </summary>
public class OllamaContextLengthTests
{
    private static OllamaShowResponse Parse(string json) =>
        JsonSerializer.Deserialize<OllamaShowResponse>(json)!;

    [Fact]
    public void The_context_length_is_read_from_the_architecture_prefixed_key()
    {
        // Shape returned for orcarouter/Qwen3.8-27B-Uncensored:latest.
        var show = Parse("""
            {
              "model_info": {
                "general.architecture": "qwen35",
                "qwen35.context_length": 262144,
                "qwen35.embedding_length": 5120
              }
            }
            """);

        Assert.Equal(262144, show.ContextLength);
    }

    [Theory]
    [InlineData("llama")]
    [InlineData("gemma3")]
    [InlineData("some.future.family")]
    public void Any_model_family_works_because_the_key_is_matched_by_suffix(string architecture)
    {
        var show = Parse($$"""
            { "model_info": { "{{architecture}}.context_length": 32768 } }
            """);

        Assert.Equal(32768, show.ContextLength);
    }

    [Fact]
    public void A_response_carrying_no_context_length_reports_zero_rather_than_a_guess()
    {
        // Zero means unknown, which is what makes the app fall back to the size
        // configured in Settings instead of acting on a number nobody reported.
        var show = Parse("""
            { "model_info": { "general.architecture": "llama" } }
            """);

        Assert.Equal(0, show.ContextLength);
    }

    [Fact]
    public void A_response_with_no_model_info_at_all_reports_zero()
    {
        Assert.Equal(0, Parse("{}").ContextLength);
    }

    [Fact]
    public void A_context_length_that_is_not_a_number_is_ignored()
    {
        var show = Parse("""
            { "model_info": { "llama.context_length": "very large" } }
            """);

        Assert.Equal(0, show.ContextLength);
    }

    [Fact]
    public void With_nothing_configured_the_model_reports_its_own_length()
    {
        Assert.Equal(262144, OllamaProvider.EffectiveContextWindow(configured: 0, modelLength: 262144));
    }

    [Fact]
    public void A_configured_window_is_what_the_request_gets()
    {
        Assert.Equal(32768, OllamaProvider.EffectiveContextWindow(configured: 32768, modelLength: 262144));
    }

    [Fact]
    public void Asking_for_more_than_the_model_was_trained_for_gets_the_model_length()
    {
        // Ollama will not give a model a longer memory than it was built with,
        // so reporting the larger figure would only mislead the context meter.
        Assert.Equal(32768, OllamaProvider.EffectiveContextWindow(configured: 999_999, modelLength: 32768));
    }

    [Fact]
    public void A_configured_window_still_applies_when_the_model_length_is_unknown()
    {
        Assert.Equal(16384, OllamaProvider.EffectiveContextWindow(configured: 16384, modelLength: 0));
    }

    [Fact]
    public void Knowing_neither_reports_nothing_rather_than_a_guess()
    {
        // Zero is what makes the app fall back to the size set in Settings.
        Assert.Equal(0, OllamaProvider.EffectiveContextWindow(configured: 0, modelLength: 0));
    }

    [Fact]
    public void A_configured_window_is_sent_as_num_ctx()
    {
        var json = JsonSerializer.Serialize(new OllamaOptions { NumCtx = 32768 });

        Assert.Contains("\"num_ctx\":32768", json);
    }

    [Fact]
    public void No_configured_window_leaves_num_ctx_out_entirely()
    {
        // Not sent as 0: Ollama would try to honour a zero-length context
        // rather than reading it as "you decide".
        var json = JsonSerializer.Serialize(new OllamaOptions { NumCtx = null });

        Assert.DoesNotContain("num_ctx", json);
    }

    [Fact]
    public void A_model_length_beyond_int_range_does_not_wrap_around()
    {
        // Reported as a long; truncating the cast would turn a huge window into
        // a negative one and read as permanently full.
        Assert.Equal(int.MaxValue, OllamaProvider.EffectiveContextWindow(configured: 0, modelLength: long.MaxValue));
    }
}
