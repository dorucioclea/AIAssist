namespace BuildingBlocks.InMemoryVectorDatabase;

public class Document
{
    public Guid Id { get; set; }
    public string Text { get; set; } = default!;
    public IList<double> Embeddings { get; set; } // Store the embedding
    public IDictionary<string, string> Metadata { get; set; } // Store metadata
}