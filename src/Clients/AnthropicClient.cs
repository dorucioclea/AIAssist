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
using Clients.Models.Anthropic;
using Clients.Options;
using Humanizer;
using Microsoft.Extensions.Options;
using Polly.Wrap;

namespace Clients;

// ref: https://docs.anthropic.com/en/api
// https://docs.anthropic.com/en/api/messages-streaming
// https://docs.anthropic.com/en/api/messages-examples

public class AnthropicClient(
    IHttpClientFactory httpClientFactory,
    IOptions<LLMOptions> options,
    ICacheModels cacheModels,
    ITokenizer tokenizer,
    AsyncPolicyWrap<HttpResponseMessage> combinedPolicy
) : ILLMClient
{
    private readonly Model _chatModel = cacheModels.GetModel(options.Value.ChatModel);
    private const int MaxRequestSizeInBytes = 100 * 1024; // 100KB

    public async Task<ChatCompletionResponse?> GetCompletionAsync(
        ChatCompletionRequest chatCompletionRequest,
        CancellationToken cancellationToken = default
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
        };

        var client = httpClientFactory.CreateClient("llm_chat_client");

        // https://github.com/App-vNext/Polly#handing-return-values-and-policytresult
        var httpResponseMessage = await combinedPolicy.ExecuteAsync(async () =>
        {
            // https://docs.anthropic.com/en/api/complete
            var response = await client.PostAsJsonAsync(
                "v1/messages",
                requestBody,
                cancellationToken: cancellationToken
            );

            return response;
        });

        var completionResponse = await httpResponseMessage.Content.ReadFromJsonAsync<AnthropicChatResponse>(
            options: JsonObjectSerializer.SnakeCaseOptions,
            cancellationToken: cancellationToken
        );

        HandleException(httpResponseMessage, completionResponse);

        var completionMessage = completionResponse.Content.FirstOrDefault(x => x.Type == "text")?.Text;

        var inputTokens = completionResponse.Usage?.InputTokens ?? 0;
        var outTokens = completionResponse.Usage?.OutputTokens ?? 0;
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
        };

        var client = httpClientFactory.CreateClient("llm_chat_client");

        var httpResponseMessage = await combinedPolicy.ExecuteAsync(async () =>
        {
            var response = await client.PostAsJsonAsync(
                "v1/messages",
                requestBody,
                cancellationToken: cancellationToken
            );

            return response;
        });

        if (httpResponseMessage.IsSuccessStatusCode)
        {
            // https://docs.anthropic.com/en/api/messages-streaming
            // Read the response as a stream
            await using var responseStream = await httpResponseMessage.Content.ReadAsStreamAsync(cancellationToken);
            using var streamReader = new StreamReader(responseStream);

            while (!streamReader.EndOfStream)
            {
                var line = await streamReader.ReadLineAsync(cancellationToken);

                if (string.IsNullOrEmpty(line) || string.IsNullOrWhiteSpace(line))
                    continue;

                if (line.StartsWith("data:"))
                {
                    var jsonData = line.Substring("data:".Length).Trim();

                    if (string.IsNullOrEmpty(jsonData))
                        continue;

                    var completionStreamResponse = JsonSerializer.Deserialize<AnthropicChatResponse>(
                        jsonData,
                        options: JsonObjectSerializer.SnakeCaseOptions
                    );

                    if (completionStreamResponse is null)
                        continue;

                    var completionMessage = completionStreamResponse.Delta?.Text;

                    if (completionMessage is null)
                        continue;

                    // when we reached to the end of the streams
                    if (completionStreamResponse.Delta?.StopReason == "end_turn")
                    {
                        //https://docs.anthropic.com/en/api/messages-streaming
                        // we have the usage in the last chunk and done state
                        var inputTokens = completionStreamResponse.Usage?.InputTokens ?? 0;
                        var outTokens = completionStreamResponse.Usage?.OutputTokens ?? 0;
                        var inputCostPerToken = _chatModel.ModelInformation.InputCostPerToken;
                        var outputCostPerToken = _chatModel.ModelInformation.OutputCostPerToken;

                        ValidateMaxToken(inputTokens + outTokens);

                        yield return new ChatCompletionResponse(
                            null,
                            new TokenUsageResponse(inputTokens, inputCostPerToken, outTokens, outputCostPerToken)
                        );
                    }
                    else
                    {
                        yield return new ChatCompletionResponse(completionMessage, null);
                    }
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    yield break;
                }
            }
        }
        else
        {
            var completionResponse = await httpResponseMessage.Content.ReadFromJsonAsync<AnthropicChatResponse>(
                cancellationToken: cancellationToken
            );
            HandleException(httpResponseMessage, completionResponse);
        }
    }

    public Task<EmbeddingsResponse?> GetEmbeddingAsync(string input, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    private void HandleException(
        HttpResponseMessage httpResponse,
        [NotNull] AnthropicChatResponse? anthropicChatResponse
    )
    {
        if (anthropicChatResponse is null)
        {
            httpResponse.EnsureSuccessStatusCode();
        }

        if (!httpResponse.IsSuccessStatusCode && anthropicChatResponse!.Error is null)
        {
            anthropicChatResponse.Error = new AnthropicError
            {
                Message = httpResponse.ReasonPhrase ?? httpResponse.StatusCode.ToString(),
                Code = ((int)httpResponse.StatusCode).ToString(),
            };
        }

        if (anthropicChatResponse!.Error is not null)
        {
            anthropicChatResponse.Error.StatusCode = (int)httpResponse.StatusCode;
        }

        if (anthropicChatResponse.Error is not null)
        {
            throw new AnthropicException(anthropicChatResponse.Error, httpResponse.StatusCode);
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
            throw new AnthropicException(
                new AnthropicError
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
            throw new AnthropicException(
                new AnthropicError
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
            throw new AnthropicException(
                new AnthropicError()
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
