using SNChat.Core.Models;
using SNChat.Core.Services;

namespace SNChat.Tests;

/// <summary>
/// Tool calls and their results used to exist only inside one request: each
/// provider built the transcript in a local list and threw it away when the
/// reply finished. Survivable for a single question and answer, because the
/// assistant summarises what it found in prose - but fatal for a loop carrying
/// on across turns, which would forget every file it read and every build it ran.
/// </summary>
public class ToolExchangePersistenceTests
{
    private static Message Exchange(
        string tool = "read_file",
        string callId = "call_abc123",
        string arguments = """{"path":"C:\\src\\main.cpp"}""",
        string result = "int main() { return 0; }") =>
        new()
        {
            Role = MessageRole.Tool,
            Content = result,
            ToolName = tool,
            ToolCallId = callId,
            ToolArguments = arguments
        };

    // --- header ---

    [Fact]
    public void The_tool_and_its_call_id_survive_the_header()
    {
        var header = MessageHeader.Format(3, Exchange())["## Message ".Length..];

        Assert.True(MessageHeader.TryParse(header, out var role, out _, out var facts));

        Assert.Equal(MessageRole.Tool, role);
        Assert.Equal("read_file", facts.ToolName);
        Assert.Equal("call_abc123", facts.ToolCallId);
    }

    [Fact]
    public void An_ordinary_message_gains_no_tool_fields()
    {
        var header = MessageHeader.Format(1, new Message { Role = MessageRole.User });

        Assert.DoesNotContain("tool=", header);
        Assert.DoesNotContain("call=", header);
    }

    [Fact]
    public void An_exchange_from_a_provider_with_no_ids_still_round_trips()
    {
        // Ollama pairs a result to its call by tool name and supplies no id.
        var header = MessageHeader.Format(1, Exchange(callId: ""))["## Message ".Length..];

        Assert.True(MessageHeader.TryParse(header, out _, out _, out var facts));
        Assert.Equal("read_file", facts.ToolName);
        Assert.Equal(string.Empty, facts.ToolCallId);
    }

    // --- body ---

    [Fact]
    public void The_arguments_and_the_result_are_told_apart_in_the_body()
    {
        var written = ToolExchangeFormat.Write(Exchange());
        var (arguments, content) = ToolExchangeFormat.Read(written);

        Assert.Contains("main.cpp", arguments);
        Assert.Equal("int main() { return 0; }", content);
    }

    [Fact]
    public void A_result_containing_a_fenced_block_is_not_mistaken_for_arguments()
    {
        // read_file returning source code is the ordinary case, and source
        // routinely contains fences.
        var message = Exchange(result: "Here is the file:\n\n```json\n{\"a\":1}\n```\ndone");

        var (arguments, content) = ToolExchangeFormat.Read(ToolExchangeFormat.Write(message));

        Assert.Contains("main.cpp", arguments);
        Assert.Contains("Here is the file", content);
        Assert.Contains("{\"a\":1}", content);
    }

    [Fact]
    public void An_exchange_recorded_before_arguments_were_kept_reads_as_all_result()
    {
        // Backwards compatibility with anything already on disk.
        var (arguments, content) = ToolExchangeFormat.Read("just the result text");

        Assert.Equal(string.Empty, arguments);
        Assert.Equal("just the result text", content);
    }

    [Fact]
    public void An_unclosed_fence_is_treated_as_result_rather_than_swallowed()
    {
        // A hand-edited file should lose nothing silently.
        var (arguments, content) = ToolExchangeFormat.Read("```json\n{\"a\":1}\nno closing fence");

        Assert.Equal(string.Empty, arguments);
        Assert.Contains("no closing fence", content);
    }

    [Fact]
    public void An_exchange_with_no_arguments_writes_no_fence()
    {
        // list_projects and read_app_log take none.
        var written = ToolExchangeFormat.Write(Exchange(arguments: ""));

        Assert.DoesNotContain("```", written);
        Assert.Equal("int main() { return 0; }", written);
    }

    [Fact]
    public void Multi_line_arguments_survive()
    {
        var pretty = "{\n  \"path\": \"a.txt\",\n  \"edits\": [\n    { \"oldText\": \"a\" }\n  ]\n}";

        var (arguments, _) = ToolExchangeFormat.Read(
            ToolExchangeFormat.Write(Exchange(arguments: pretty)));

        Assert.Contains("oldText", arguments);
        Assert.Contains("edits", arguments);
    }

    // --- model ---

    [Fact]
    public void A_tool_message_says_which_tool_it_was()
    {
        Assert.Equal("Tool · read_file", Exchange().RoleLabel);
    }

    [Fact]
    public void Cloning_keeps_the_tool_details()
    {
        // Branching a conversation must not quietly drop what the tools returned.
        var clone = Exchange().Clone();

        Assert.Equal("read_file", clone.ToolName);
        Assert.Equal("call_abc123", clone.ToolCallId);
        Assert.Contains("main.cpp", clone.ToolArguments);
    }

    [Fact]
    public void An_automatic_continuation_is_not_shown_as_the_users_own_message()
    {
        // It has to be sent as a user turn for the model to answer it, but it did
        // not come from the user. Whoever reads the conversation afterwards
        // should be able to tell which turns were theirs.
        var nudge = new Message { Role = MessageRole.User, IsAutoContinue = true };

        Assert.Equal("Continued automatically", nudge.RoleLabel);
    }

    [Fact]
    public void An_automatic_continuation_is_still_marked_when_read_back()
    {
        var header = MessageHeader.Format(
            2, new Message { Role = MessageRole.User, IsAutoContinue = true })["## Message ".Length..];

        Assert.True(MessageHeader.TryParse(header, out _, out _, out var facts));
        Assert.True(facts.IsAutoContinue);
    }

    [Fact]
    public void A_message_the_user_typed_carries_no_automatic_marker()
    {
        var header = MessageHeader.Format(1, new Message { Role = MessageRole.User });

        Assert.DoesNotContain("auto=", header);
    }

    [Fact]
    public void Only_a_tool_message_reports_itself_as_an_exchange()
    {
        Assert.True(Exchange().IsToolExchange);
        Assert.False(new Message { Role = MessageRole.Assistant }.IsToolExchange);
    }
}
