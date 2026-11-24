using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using FlowCapture.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace FlowCapture.Infrastructure.Storage;

public sealed class StorageOptions
{
    public const string Section = "Storage";
    /// <summary>FileSystem | AzureBlob</summary>
    public string Provider { get; set; } = "FileSystem";
    public string RootPath { get; set; } = "App_Data/blobs";
    public string? ConnectionString { get; set; }
    public string Container { get; set; } = "flowcapture";
}

/// <summary>Development and on-premises storage. Put RootPath on an encrypted volume in production.</summary>
public sealed class FileSystemBlobStore : IBlobStore
{
    private readonly string _root;

    public FileSystemBlobStore(IOptions<StorageOptions> options)
    {
        _root = Path.GetFullPath(options.Value.RootPath);
        Directory.CreateDirectory(_root);
    }

    public async Task SaveAsync(string path, Stream content, string? contentType, CancellationToken ct)
    {
        var full = Resolve(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            await content.CopyToAsync(file, ct);
        File.Move(temp, full, overwrite: true);
    }

    public Task<Stream> OpenReadAsync(string path, CancellationToken ct)
    {
        var full = Resolve(path);
        if (!File.Exists(full)) throw new FileNotFoundException("The stored file was not found.", path);
        return Task.FromResult<Stream>(new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan));
    }

    public Task<bool> ExistsAsync(string path, CancellationToken ct) => Task.FromResult(File.Exists(Resolve(path)));

    public Task DeleteAsync(string path, CancellationToken ct)
    {
        var full = Resolve(path);
        if (File.Exists(full)) File.Delete(full);
        return Task.CompletedTask;
    }

    private string Resolve(string path)
    {
        var full = Path.GetFullPath(Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("The path is outside the storage root.", nameof(path));
        return full;
    }
}

/// <summary>Azure Blob Storage. Encryption at rest is provided by the storage account (customer-managed keys optional).</summary>
public sealed class AzureBlobStore : IBlobStore
{
    private readonly BlobContainerClient _container;
    private bool _ready;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AzureBlobStore(IOptions<StorageOptions> options)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.ConnectionString))
            throw new InvalidOperationException("Storage:ConnectionString is required for the AzureBlob provider.");
        _container = new BlobContainerClient(o.ConnectionString, o.Container);
    }

    public async Task SaveAsync(string path, Stream content, string? contentType, CancellationToken ct)
    {
        await EnsureAsync(ct);
        var blob = _container.GetBlobClient(path);
        await blob.UploadAsync(content, new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = contentType } }, ct);
    }

    public async Task<Stream> OpenReadAsync(string path, CancellationToken ct)
    {
        await EnsureAsync(ct);
        return await _container.GetBlobClient(path).OpenReadAsync(cancellationToken: ct);
    }

    public async Task<bool> ExistsAsync(string path, CancellationToken ct)
    {
        await EnsureAsync(ct);
        return (await _container.GetBlobClient(path).ExistsAsync(ct)).Value;
    }

    public async Task DeleteAsync(string path, CancellationToken ct)
    {
        await EnsureAsync(ct);
        await _container.GetBlobClient(path).DeleteIfExistsAsync(cancellationToken: ct);
    }

    private async Task EnsureAsync(CancellationToken ct)
    {
        if (_ready) return;
        await _gate.WaitAsync(ct);
        try
        {
            if (!_ready)
            {
                await _container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: ct);
                _ready = true;
            }
        }
        finally { _gate.Release(); }
    }
}
