using System.Text;
using AIAssistant.Contracts;
using AIAssistant.Contracts.CodeAssist;

namespace AIAssistant.Services.CodeAssistStrategies;

public class TreeSitterCodeAssistSummary(
    IContextService contextService,
    IPromptManager promptManager,
    ILLMClientManager llmClientManager
) : ICodeAssist
{
    public Task LoadInitCodeFiles(string contextWorkingDirectory, IList<string>? codeFiles)
    {
        contextService.AddContextFolder(contextWorkingDirectory);
        contextService.AddOrUpdateFiles(codeFiles);

        return Task.CompletedTask;
    }

    public Task AddOrUpdateCodeFiles(IList<string>? codeFiles)
    {
        if (codeFiles is null || codeFiles.Count == 0)
            return Task.CompletedTask;

        contextService.AddOrUpdateFiles(codeFiles);

        return Task.CompletedTask;
    }

    public Task<IEnumerable<string>> GetCodeTreeContents(IList<string>? codeFiles)
    {
        if (codeFiles is null || !codeFiles.Any())
            return Task.FromResult(Enumerable.Empty<string>());

        var filesTreeToUpdate = contextService
            .GetFiles(codeFiles)
            .Select(x => promptManager.AddCodeBlock(x.CodeFileMap?.TreeSitterSummarizeCode ?? string.Empty));

        return Task.FromResult(filesTreeToUpdate);
    }

    public async IAsyncEnumerable<string?> QueryChatCompletionAsync(string userQuery)
    {
        var summaryTreeCodes = contextService
            .GetAllFiles()
            .Select(x => x.CodeFileMap.TreeSitterSummarizeCode)
            .Where(x => !string.IsNullOrEmpty(x))
            .ToList();

        var systemPrompt = promptManager.GetSystemPrompt(
            summaryTreeCodes,
            llmClientManager.ChatModel.ModelOption.CodeAssistType,
            llmClientManager.ChatModel.ModelOption.CodeDiffType
        );

        // Generate a response from the language model (e.g., OpenAI or Llama)
        var completionStreams = llmClientManager.GetCompletionStreamAsync(
            userQuery: userQuery,
            systemPrompt: systemPrompt
        );

        StringBuilder sb = new StringBuilder();
        await foreach (var streamItem in completionStreams)
        {
            sb.Append(streamItem);
            yield return streamItem;
        }
    }
}
