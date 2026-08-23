using System.Collections.Immutable;
using System.Text;

namespace WindowRecall.Core;

/// <summary>File operations used to make atomic-write behavior deterministic in tests.</summary>
public interface IAtomicFileSystem
{
    Task WriteNewFileAsync(string path, string contents, CancellationToken cancellationToken);
    void ReplaceFile(string temporaryPath, string destinationPath);
    Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken);
    IEnumerable<string> EnumerateFiles(string directory, string pattern);
    void CreateDirectory(string path);
    void DeleteFile(string path);
}

/// <summary>Physical local filesystem implementation with flushed temp writes and same-volume atomic rename.</summary>
public sealed class PhysicalAtomicFileSystem : IAtomicFileSystem
{
    public async Task WriteNewFileAsync(string path, string contents, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await using StreamWriter writer = new(stream, new UTF8Encoding(false));
        await writer.WriteAsync(contents.AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    public void ReplaceFile(string temporaryPath, string destinationPath) => File.Move(temporaryPath, destinationPath, overwrite: true);
    public Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken) => File.ReadAllTextAsync(path, cancellationToken);
    public IEnumerable<string> EnumerateFiles(string directory, string pattern) => Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly);
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public void DeleteFile(string path) => File.Delete(path);
}

/// <summary>Stores one validated JSON document per profile under a configurable local data root.</summary>
public sealed class JsonFileProfileStore : IProfileStore
{
    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CLOCK$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "COM¹", "COM²", "COM³", "LPT¹", "LPT²", "LPT³",
    };

    private readonly string dataRoot;
    private readonly IAtomicFileSystem fileSystem;

    public JsonFileProfileStore(string dataRoot, IAtomicFileSystem? fileSystem = null)
    {
        if (string.IsNullOrWhiteSpace(dataRoot)) throw new ArgumentException("A data root is required.", nameof(dataRoot));
        this.dataRoot = Path.GetFullPath(dataRoot);
        this.fileSystem = fileSystem ?? new PhysicalAtomicFileSystem();
    }

    public async Task SaveAsync(string profileId, LayoutProfile profile, CancellationToken cancellationToken = default)
    {
        string destination = GetPath(profileId);
        fileSystem.CreateDirectory(dataRoot);
        string temporary = Path.Combine(dataRoot, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await fileSystem.WriteNewFileAsync(temporary, ProfileJsonSerializer.Serialize(profile), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            fileSystem.ReplaceFile(temporary, destination);
        }
        finally
        {
            fileSystem.DeleteFile(temporary);
        }
    }

    public async Task<LayoutProfile> LoadAsync(string profileId, CancellationToken cancellationToken = default) =>
        ProfileJsonSerializer.Deserialize(await fileSystem.ReadAllTextAsync(GetPath(profileId), cancellationToken).ConfigureAwait(false));

    public Task<ImmutableArray<string>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(dataRoot)) return Task.FromResult(ImmutableArray<string>.Empty);
        ImmutableArray<string> result = fileSystem.EnumerateFiles(dataRoot, "*.json")
            .Select(path => Path.GetFileNameWithoutExtension(path)!).Order(StringComparer.Ordinal).ToImmutableArray();
        return Task.FromResult(result);
    }

    public Task DeleteAsync(string profileId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        fileSystem.DeleteFile(GetPath(profileId));
        return Task.CompletedTask;
    }

    private string GetPath(string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId) ||
            profileId is "." or ".." ||
            profileId.EndsWith('.') ||
            profileId.EndsWith(' ') ||
            profileId.Any(character => character < 32 || "<>:\"/\\|?*".Contains(character)) ||
            ReservedWindowsNames.Contains(profileId.Split('.')[0]))
            throw new ArgumentException("Profile id must be a safe file name.", nameof(profileId));
        return Path.Combine(dataRoot, profileId + ".json");
    }
}
