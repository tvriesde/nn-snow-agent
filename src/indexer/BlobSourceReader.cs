using System.Text.Json;
using Azure.Storage.Blobs;
using Helpdesk.Core;
using Microsoft.Extensions.Configuration;

namespace Helpdesk.Indexer;

public sealed class BlobSourceReader(BlobServiceClient service, IConfiguration configuration) : ISourceReader
{
    private readonly BlobContainerClient container = service.GetBlobContainerClient(
        configuration["Source:ContainerName"] ?? "servicenow");

    public async Task<SourceManifest> ReadManifestAsync(CancellationToken cancellationToken)
    {
        var bytes = await ReadBatchAsync("manifest.json", cancellationToken);
        return JsonSerializer.Deserialize<SourceManifest>(bytes, DeltaIndexer.JsonOptions)
            ?? throw new InvalidDataException("Source manifest is null.");
    }

    public async Task<byte[]> ReadBatchAsync(string path, CancellationToken cancellationToken)
    {
        var blob = container.GetBlobClient(path);
        var properties = await blob.GetPropertiesAsync(cancellationToken: cancellationToken);
        if (properties.Value.ContentLength > 4 * 1024 * 1024)
            throw new InvalidDataException("Source blob exceeds the 4 MB batch limit.");
        var content = await blob.DownloadContentAsync(cancellationToken);
        return content.Value.Content.ToArray();
    }
}
