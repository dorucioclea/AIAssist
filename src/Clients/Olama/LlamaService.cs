using System.Net.Http.Json;
using Clients.Olama.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Clients.Olama;

public class LlamaService(HttpClient client, ILogger<LlamaService> logger) : ILanguageModelService
{
    public async Task<string> GetCompletionAsync(string userQuery, string codeContext)
    {
        var userContent = PromptHelper.GenerateSuggestedCodePrompt(codeContext, userQuery);

        var requestBody = new
        {
            model = "llama3.1",
            messages = new[]
            {
                new
                {
                    role = "system",
                    content = "You are an expert code assistant. Your job is to help users analyze and improve their code.",
                },
                new { role = "user", content = userContent },
            },
            temperature = 0.5,
        };

        logger.LogInformation("Sending completion request to LLaMA");

        // https://ollama.com/blog/openai-compatibility
        // https://www.youtube.com/watch?v=38jlvmBdBrU
        // https://platform.openai.com/docs/api-reference/chat/create
        // https://github.com/ollama/ollama/blob/main/docs/api.md#generate-a-chat-completion
        var response = await client.PostAsJsonAsync("v1/chat/completions", requestBody);

        var responseContent = await response.Content.ReadAsStringAsync();

        if (response.IsSuccessStatusCode)
        {
            logger.LogInformation(
                "Received successful response from LLaMA. ResponseContent: {ResponseContent}",
                responseContent
            );
        }
        else
        {
            logger.LogError("Error in LLaMA completion response: {ResponseContent}", responseContent);
        }

        var completionResponse = JsonConvert.DeserializeObject<LlamaCompletionResponse>(responseContent);

        return completionResponse?.Choices.FirstOrDefault()?.Text.Trim() ?? string.Empty;
    }

    public async Task<string> GetEmbeddingAsync(string input)
    {
        var requestBody = new
        {
            input = new[] { input },
            model = "llama3.1",
            encoding_format = "float",
        };

        logger.LogInformation("Sending embedding request to LLaMA");

        // https://github.com/ollama/ollama/blob/main/docs/api.md#generate-embeddings
        // https://platform.openai.com/docs/api-reference/embeddings
        var response = await client.PostAsJsonAsync("v1/embeddings", requestBody);

        var responseContent = await response.Content.ReadAsStringAsync();

        if (response.IsSuccessStatusCode)
        {
            logger.LogInformation(
                "Received successful embedding response from LLaMA. ResponseContent: {ResponseContent}",
                responseContent
            );
        }
        else
        {
            logger.LogError("Error in LLaMA embedding response: {ResponseContent}", responseContent);
        }

        var embeddingResponse = JsonConvert.DeserializeObject<LlamaEmbeddingResponse>(responseContent);

        return string.Join(",", embeddingResponse?.Data.FirstOrDefault()?.Embedding ?? new List<double>());
    }
}
