using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using BuildingBlocks.LLM;
using BuildingBlocks.Serialization;
using BuildingBlocks.Utils;
using Clients.Contracts;
using Clients.Dtos;
using Clients.Models;
using Clients.Models.OpenAI;
using Clients.Models.OpenAI.Completion;
using Clients.Models.OpenAI.Embeddings;
using Clients.Options;
using Humanizer;
using Microsoft.Extensions.Options;
using Polly.Wrap;

namespace Clients;

// Ref: https://platform.openai.com/docs/api-reference/

public class OpenAiClient(
    IHttpClientFactory httpClientFactory,
    IOptions<LLMOptions> options,
    ICacheModels cacheModels,
    ITokenizer tokenizer,
    AsyncPolicyWrap<HttpResponseMessage> combinedPolicy
) : ILLMClient
{
    private readonly Model _chatModel = cacheModels.GetModel(options.Value.ChatModel);
    private readonly Model _embeddingModel = cacheModels.GetModel(options.Value.EmbeddingsModel);
    private const int MaxRequestSizeInBytes = 100 * 1024; // 100KB

    public async Task<ChatCompletionResponse?> GetCompletionAsync(
        ChatCompletionRequest chatCompletionRequest,
        CancellationToken cancellationToken = default
    )
    {
        await ValidateMaxInputToken(chatCompletionRequest);
        ValidateRequestSizeAndContent(chatCompletionRequest);

        // https://platform.openai.com/docs/api-reference/chat/create
        var requestBody = new
        {
            model = _chatModel.Name.Trim(),
            messages = chatCompletionRequest.Items.Select(x => new
            {
                role = x.Role.Humanize(LetterCasing.LowerCase),
                content = x.Prompt,
            }),
            temperature = _chatModel.ModelOption.Temperature,
        };

        var client = httpClientFactory.CreateClient("llm_chat_client");

        // https://github.com/App-vNext/Polly#handing-return-values-and-policytresult
        var httpResponseMessage = await combinedPolicy.ExecuteAsync(async () =>
        {
            // https://platform.openai.com/docs/api-reference/chat/create
            var response = await client.PostAsJsonAsync(
                "v1/chat/completions",
                requestBody,
                cancellationToken: cancellationToken
            );

            return response;
        });

        var completionResponse = await httpResponseMessage.Content.ReadFromJsonAsync<OpenAIChatResponse>(
            options: JsonObjectSerializer.SnakeCaseOptions,
            cancellationToken: cancellationToken
        );

        HandleException(httpResponseMessage, completionResponse);

        var completionMessage = completionResponse
            .Choices?.FirstOrDefault(x => x.Message.Role == RoleType.Assistant)
            ?.Message.Content;

        var inputTokens = completionResponse.Usage?.PromptTokens ?? 0;
        var outTokens = completionResponse.Usage?.CompletionTokens ?? 0;
        var inputCostPerToken = _chatModel.ModelInformation.InputCostPerToken;
        var outputCostPerToken = _chatModel.ModelInformation.OutputCostPerToken;

        ValidateMaxToken(inputTokens + outTokens);

        return new ChatCompletionResponse(
            completionMessage,
            new TokenUsageResponse(inputTokens, inputCostPerToken, outTokens, outputCostPerToken)
        );
    }

    public async IAsyncEnumerable<ChatCompletionResponse?> GetCompletionStreamAsync(
        ChatCompletionRequest chatCompletionRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        await ValidateMaxInputToken(chatCompletionRequest);
        ValidateRequestSizeAndContent(chatCompletionRequest);

        var requestBody = new
        {
            model = _chatModel.Name.Trim(),
            messages = chatCompletionRequest.Items.Select(x => new
            {
                role = x.Role.Humanize(LetterCasing.LowerCase),
                content = x.Prompt,
            }),
            temperature = _chatModel.ModelOption.Temperature,
            stream = true,
            // https://cookbook.openai.com/examples/how_to_stream_completions#4-how-to-get-token-usage-data-for-streamed-chat-completion-response
            stream_options = new { include_usage = true },
        };

        var client = httpClientFactory.CreateClient("llm_chat_client");

        // Execute the policy with streaming support
        var httpResponseMessage = await combinedPolicy.ExecuteAsync(async () =>
        {
            var response = await client.PostAsJsonAsync(
                "v1/chat/completions",
                requestBody,
                cancellationToken: cancellationToken
            );

            return response;
        });

        if (httpResponseMessage.IsSuccessStatusCode)
        {
            // https://platform.openai.com/docs/api-reference/chat/create#chat-create-stream
            // https://cookbook.openai.com/examples/how_to_stream_completions
            // Read the response stream
            await using var responseStream = await httpResponseMessage.Content.ReadAsStreamAsync(cancellationToken);

            using var streamReader = new StreamReader(responseStream);

            while (!streamReader.EndOfStream)
            {
                var line = await streamReader.ReadLineAsync(cancellationToken);

                if (string.IsNullOrEmpty(line) || string.IsNullOrWhiteSpace(line))
                    continue;

                // when we reached to the end of the streams
                if (line.StartsWith("data: [DONE]"))
                {
                    continue;
                }

                // Parse the streaming data (assume JSON format)
                if (line.StartsWith("data: "))
                {
                    var jsonData = line.Substring("data: ".Length);
                    if (string.IsNullOrEmpty(jsonData))
                        continue;

                    var completionStreamResponse = JsonSerializer.Deserialize<OpenAIChatResponse>(
                        jsonData,
                        options: JsonObjectSerializer.SnakeCaseOptions
                    );

                    if (completionStreamResponse is null)
                        continue;

                    var choice = completionStreamResponse.Choices?.FirstOrDefault();

                    if (choice?.Delta is not null)
                    {
                        var completionMessage = choice.Delta.Content;

                        if (completionMessage is null)
                            continue;

                        yield return new ChatCompletionResponse(completionMessage, null);
                    }
                    else if (completionStreamResponse.Usage is not null)
                    {
                        // Capture the `usage` data from the final chunk and after done
                        var inputTokens = completionStreamResponse.Usage?.PromptTokens ?? 0;
                        var outTokens = completionStreamResponse.Usage?.CompletionTokens ?? 0;
                        var inputCostPerToken = _chatModel.ModelInformation.InputCostPerToken;
                        var outputCostPerToken = _chatModel.ModelInformation.OutputCostPerToken;

                        ValidateMaxToken(inputTokens + outTokens);

                        yield return new ChatCompletionResponse(
                            null,
                            new TokenUsageResponse(inputTokens, inputCostPerToken, outTokens, outputCostPerToken)
                        );
                    }
                }
            }
        }
        else
        {
            var completionResponse = await httpResponseMessage.Content.ReadFromJsonAsync<OpenAIChatResponse>(
                cancellationToken: cancellationToken
            );
            HandleException(httpResponseMessage, completionResponse);
        }
    }

    public async Task<EmbeddingsResponse?> GetEmbeddingAsync(
        string input,
        CancellationToken cancellationToken = default
    )
    {
        await ValidateMaxInputToken(input);
        ValidateRequestSizeAndContent(input);

        var requestBody = new { input = new[] { input }, model = _embeddingModel.Name.Trim() };

        var client = httpClientFactory.CreateClient("llm_embeddings_client");

        // https://github.com/App-vNext/Polly#handing-return-values-and-policytresult
        var httpResponseMessage = await combinedPolicy.ExecuteAsync(async () =>
        {
            // https://platform.openai.com/docs/api-reference/embeddings
            var response = await client.PostAsJsonAsync(
                "v1/embeddings",
                requestBody,
                cancellationToken: cancellationToken
            );

            return response;
        });

        var embeddingResponse = await httpResponseMessage.Content.ReadFromJsonAsync<OpenAIEmbeddingResponse>(
            options: JsonObjectSerializer.SnakeCaseOptions,
            cancellationToken: cancellationToken
        );

        HandleException(httpResponseMessage, embeddingResponse);

        var embedding = embeddingResponse.Data.FirstOrDefault()?.Embedding ?? new List<double>();

        var inputTokens = embeddingResponse.Usage?.PromptTokens ?? 0;
        var outTokens = embeddingResponse.Usage?.CompletionTokens ?? 0;
        var inputCostPerToken = _embeddingModel.ModelInformation.InputCostPerToken;
        var outputCostPerToken = _embeddingModel.ModelInformation.OutputCostPerToken;

        ValidateMaxToken(inputTokens + outTokens);

        return new EmbeddingsResponse(
            embedding,
            new TokenUsageResponse(inputTokens, inputCostPerToken, outTokens, outputCostPerToken)
        );
    }

    private void HandleException(HttpResponseMessage httpResponse, [NotNull] OpenAIBaseResponse? opneaiBaseResponse)
    {
        if (opneaiBaseResponse is null)
        {
            httpResponse.EnsureSuccessStatusCode();
        }

        if (!httpResponse.IsSuccessStatusCode && opneaiBaseResponse!.Error is null)
        {
            opneaiBaseResponse.Error = new OpenAIError
            {
                Message = httpResponse.ReasonPhrase ?? httpResponse.StatusCode.ToString(),
                Code = ((int)httpResponse.StatusCode).ToString(),
            };
        }

        if (opneaiBaseResponse!.Error is not null)
        {
            opneaiBaseResponse.Error.StatusCode = (int)httpResponse.StatusCode;
        }

        if (opneaiBaseResponse.Error is not null)
        {
            throw new OpenAIException(opneaiBaseResponse.Error, httpResponse.StatusCode);
        }
    }

    private Task ValidateMaxInputToken(ChatCompletionRequest chatCompletionRequest)
    {
        return ValidateMaxInputToken(string.Concat(chatCompletionRequest.Items.Select(x => x.Prompt)));
    }

    private async Task ValidateMaxInputToken(string input)
    {
        var inputTokenCount = await tokenizer.GetTokenCount(input);

        if (
            _chatModel.ModelInformation.MaxInputTokens > 0
            && inputTokenCount > _chatModel.ModelInformation.MaxInputTokens
        )
        {
            throw new OpenAIException(
                new OpenAIError
                {
                    StatusCode = (int)HttpStatusCode.BadRequest,
                    Message =
                        $"'max_input_token' count: {inputTokenCount.FormatCommas()} is larger than configured 'max_input_token' count: {_chatModel.ModelInformation.MaxInputTokens.FormatCommas()}, if you need more tokens change the configuration.",
                },
                HttpStatusCode.BadRequest
            );
        }
    }

    private void ValidateMaxToken(int maxTokenCount)
    {
        if (_chatModel.ModelInformation.MaxTokens > 0 && maxTokenCount > _chatModel.ModelInformation.MaxTokens)
        {
            throw new OpenAIException(
                new OpenAIError
                {
                    StatusCode = (int)HttpStatusCode.BadRequest,
                    Message =
                        $"'max_token' count: {maxTokenCount.FormatCommas()} is larger than configured 'max_token' count: {_chatModel.ModelInformation.MaxTokens.FormatCommas()}, if you need more tokens change the configuration.",
                },
                HttpStatusCode.BadRequest
            );
        }
    }

    private void ValidateRequestSizeAndContent(ChatCompletionRequest chatCompletionRequest)
    {
        ValidateRequestSizeAndContent(string.Concat(chatCompletionRequest.Items.Select(x => x.Prompt)));
    }

    private void ValidateRequestSizeAndContent(string input)
    {
        var requestBodySizeInBytes = System.Text.Encoding.UTF8.GetByteCount(input);

        if (requestBodySizeInBytes > MaxRequestSizeInBytes)
        {
            throw new OpenAIException(
                new OpenAIError()
                {
                    StatusCode = (int)HttpStatusCode.BadRequest,
                    Message =
                        $"Request size {
                        requestBodySizeInBytes
                    } bytes exceeds the 100KB limit.",
                },
                HttpStatusCode.BadRequest
            );
        }
    }
}
