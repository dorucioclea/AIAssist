using System.Text.Json.Serialization;

namespace Clients.OpenAI.Models;

public class OpenAiCompletionChoice
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;
}
