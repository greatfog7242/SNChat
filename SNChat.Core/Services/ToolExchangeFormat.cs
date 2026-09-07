using System.Text;
using SNChat.Core.Models;

namespace SNChat.Core.Services;

/// <summary>
/// How a tool exchange is written into a stored conversation.
///
/// The call's arguments go in the body rather than the header line, because
/// they are JSON: often long, and full of the semicolons and brackets the
/// header's metadata field uses as its own punctuation. A fenced block keeps
/// them readable to a person opening the file and unambiguous to parse.
///
///     ```json
///     {"path":"C:\\src\\main.cpp"}
///     ```
///     the text the tool returned
///
/// Arguments are kept rather than dropped because the assistant's original call
/// has to be rebuilt when the conversation is sent again: an OpenAI-shaped API
/// rejects a tool result that matches no call, and sending an empty argument
/// list instead would misreport the conversation's own history back to it.
/// </summary>
public static class ToolExchangeFormat
{
    private const string Fence = "```json";
    private const string FenceEnd = "```";

    public static string Write(Message message)
    {
        if (string.IsNullOrWhiteSpace(message.ToolArguments))
            return message.Content;

        var sb = new StringBuilder();
        sb.AppendLine(Fence);
        sb.AppendLine(message.ToolArguments.Trim());
        sb.AppendLine(FenceEnd);
        sb.Append(message.Content);

        return sb.ToString();
    }

    /// <summary>
    /// Splits a stored body back into the arguments and the result. A body with
    /// no fenced block is all result, which is what an exchange recorded before
    /// arguments were kept looks like.
    /// </summary>
    public static (string Arguments, string Content) Read(string body)
    {
        var lines = body.Replace("\r\n", "\n").Split('\n');

        if (lines.Length == 0 || lines[0].Trim() != Fence)
            return (string.Empty, body.Trim());

        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() != FenceEnd)
                continue;

            var arguments = string.Join("\n", lines[1..i]).Trim();
            var content = string.Join("\n", lines[(i + 1)..]).Trim();

            return (arguments, content);
        }

        // Opened and never closed - a hand-edited file. Treat the whole thing as
        // the result rather than silently swallowing it as arguments.
        return (string.Empty, body.Trim());
    }
}
