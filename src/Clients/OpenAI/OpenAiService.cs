using System.Net.Http.Json;
using Clients.OpenAI.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Clients.OpenAI;

public class OpenAiService(HttpClient client, ILogger<OpenAiService> logger) : ILanguageModelService
{
    public async Task<string> GetCompletionAsync(string userQuery, string codeContext)
    {
        // https://platform.openai.com/docs/api-reference/chat/create
        var requestBody = new
        {
            model = "gpt-3.5-turbo",
            messages = new[]
            {
                new
                {
                    role = "system",
                    content = "You are an expert code assistant. Your job is to help users analyze and improve their code.",
                },
                new { role = "user", content = PromptHelper.GenerateSuggestedCodePrompt(codeContext, userQuery) },
            },
            temperature = 0.5,
        };

        logger.LogInformation("Sending completion request to OpenAI");

        var response = await client.PostAsJsonAsync("v1/chat/completions", requestBody);
        var responseContent = await response.Content.ReadAsStringAsync();

        if (response.IsSuccessStatusCode)
        {
            logger.LogInformation(
                "Received successful response from OpenAI. ResponseContent: {ResponseContent}",
                responseContent
            );
        }
        else
        {
            logger.LogError("Error in OpenAI completion response: {ResponseContent}", responseContent);
        }

        var completionResponse = JsonConvert.DeserializeObject<OpenAiCompletionResponse>(responseContent);

        return completionResponse?.Choices.FirstOrDefault()?.Text.Trim() ?? string.Empty;
    }

    public async Task<string> GetEmbeddingAsync(string input)
    {
        var requestBody = new { input = new[] { input }, model = "text-embedding-ada-002" };

        logger.LogInformation("Sending embedding request to OpenAI");

        // https://platform.openai.com/docs/api-reference/embeddings
        var response = await client.PostAsJsonAsync("v1/embeddings", requestBody);
        var responseContent = await response.Content.ReadAsStringAsync();

        if (response.IsSuccessStatusCode)
        {
            logger.LogInformation(
                "Received successful embedding response from OpenAI. ResponseContent: {ResponseContent}",
                responseContent
            );
        }
        else
        {
            logger.LogError("Error in OpenAI embedding response: {ResponseContent}", responseContent);
        }

        var embeddingResponse = JsonConvert.DeserializeObject<OpenAiEmbeddingResponse>(responseContent);

        return string.Join(",", embeddingResponse?.Data.FirstOrDefault()?.Embedding ?? new List<double>());
    }
}
