namespace Clients.Ollama.Models.Completion;

public class LlamaCompletionChoice
{
    public int Index { get; set; }
    public LlamaCompletionMessage Message { get; set; } = new();
    public string FinishReason { get; set; } = string.Empty;
}
