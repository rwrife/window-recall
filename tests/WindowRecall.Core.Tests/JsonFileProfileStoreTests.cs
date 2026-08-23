using WindowRecall.Core;

namespace WindowRecall.Core.Tests;

public sealed class JsonFileProfileStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "WindowRecall.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SaveAndLoad_RoundTripsAndListsProfile()
    {
        JsonFileProfileStore store = new(root);
        await store.SaveAsync("desk", ProfileFixture.Create(persistTitles: true));

        LayoutProfile loaded = await store.LoadAsync("desk");
        Assert.Equal("Desk", loaded.Name);
        Assert.Equal("Private document", loaded.Windows[0].Title);
        Assert.Equal(["desk"], (await store.ListAsync()).ToArray());
    }

    [Fact]
    public async Task FailedReplacement_PreservesExistingProfile()
    {
        JsonFileProfileStore initialStore = new(root);
        await initialStore.SaveAsync("desk", ProfileFixture.Create(persistTitles: true, title: "original"));
        JsonFileProfileStore failingStore = new(root, new FailingReplaceFileSystem());

        await Assert.ThrowsAsync<IOException>(() => failingStore.SaveAsync("desk", ProfileFixture.Create(persistTitles: true, title: "replacement")));

        LayoutProfile preserved = await initialStore.LoadAsync("desk");
        Assert.Equal("original", preserved.Windows[0].Title);
        Assert.Empty(Directory.EnumerateFiles(root, "*.tmp"));
    }

    [Fact]
    public async Task InterruptedTempWrite_PreservesExistingProfile()
    {
        JsonFileProfileStore initialStore = new(root);
        await initialStore.SaveAsync("desk", ProfileFixture.Create(persistTitles: true, title: "original"));
        JsonFileProfileStore failingStore = new(root, new InterruptedWriteFileSystem());

        await Assert.ThrowsAsync<OperationCanceledException>(() => failingStore.SaveAsync("desk", ProfileFixture.Create(persistTitles: true, title: "replacement")));

        Assert.Equal("original", (await initialStore.LoadAsync("desk")).Windows[0].Title);
        Assert.Empty(Directory.EnumerateFiles(root, "*.tmp"));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("nul.json")]
    [InlineData("COM1")]
    [InlineData("COM¹")]
    [InlineData("LPT³.txt")]
    [InlineData("desk.")]
    [InlineData("desk ")]
    [InlineData("../escape")]
    [InlineData("folder\\escape")]
    public async Task NonPortableOrTraversingProfileId_IsRejected(string profileId)
    {
        JsonFileProfileStore store = new(root);
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(profileId, ProfileFixture.Create()));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private class DelegatingFileSystem : IAtomicFileSystem
    {
        protected PhysicalAtomicFileSystem Inner { get; } = new();
        public virtual Task WriteNewFileAsync(string path, string contents, CancellationToken cancellationToken) => Inner.WriteNewFileAsync(path, contents, cancellationToken);
        public virtual void ReplaceFile(string temporaryPath, string destinationPath) => Inner.ReplaceFile(temporaryPath, destinationPath);
        public Task<string> ReadAllTextAsync(string path, CancellationToken cancellationToken) => Inner.ReadAllTextAsync(path, cancellationToken);
        public IEnumerable<string> EnumerateFiles(string directory, string pattern) => Inner.EnumerateFiles(directory, pattern);
        public void CreateDirectory(string path) => Inner.CreateDirectory(path);
        public void DeleteFile(string path) => Inner.DeleteFile(path);
    }

    private sealed class FailingReplaceFileSystem : DelegatingFileSystem
    {
        public override void ReplaceFile(string temporaryPath, string destinationPath) => throw new IOException("Injected replacement failure.");
    }

    private sealed class InterruptedWriteFileSystem : DelegatingFileSystem
    {
        public override async Task WriteNewFileAsync(string path, string contents, CancellationToken cancellationToken)
        {
            await Inner.WriteNewFileAsync(path, contents[..(contents.Length / 2)], cancellationToken);
            throw new OperationCanceledException("Injected interrupted write.");
        }
    }
}
