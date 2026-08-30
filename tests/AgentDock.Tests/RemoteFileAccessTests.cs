using System.IO;
using AgentDock.Services.Abstractions;
using AgentDock.Services.Remote;
using Xunit;

namespace AgentDock.Tests;

/// <summary>
/// Tests for the file-serving guards.
///
/// Everything a client sends is untrusted network input. The containment check is the only thing
/// between a paired peer and the rest of the server's disk, so the traversal cases below are the
/// most important tests in the remote feature — a regression here turns a convenience feature
/// into arbitrary file read.
/// </summary>
public class RemoteFileAccessTests : IDisposable
{
    private readonly string _root;
    private readonly string _outside;
    private readonly RemoteFileAccess _access;

    public RemoteFileAccessTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "agentdock-remote-tests", Guid.NewGuid().ToString("N"));

        _root = Path.Combine(baseDir, "project");
        _outside = Path.Combine(baseDir, "secrets");

        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
        Directory.CreateDirectory(Path.Combine(_root, "src"));

        File.WriteAllText(Path.Combine(_root, "readme.md"), "# hello");
        File.WriteAllText(Path.Combine(_root, "src", "app.cs"), "class App { }");
        File.WriteAllText(Path.Combine(_outside, "id_rsa"), "PRIVATE KEY");

        // A sibling directory whose name starts with the root's name — the classic prefix-match
        // mistake. "project-evil" must not count as being inside "project".
        Directory.CreateDirectory(baseDir + Path.DirectorySeparatorChar + "project-evil");
        File.WriteAllText(Path.Combine(baseDir, "project-evil", "loot.txt"), "loot");

        _access = new RemoteFileAccess(_root, new NullLog(), new NullPerf());
    }

    public void Dispose()
    {
        try
        {
            var baseDir = Directory.GetParent(_root)!.FullName;
            Directory.Delete(baseDir, recursive: true);
        }
        catch
        {
            // Temp cleanup is best-effort.
        }
    }

    // ---------------------------------------------------------------- containment

    [Theory]
    [InlineData("../secrets/id_rsa")]
    [InlineData("..\\secrets\\id_rsa")]
    [InlineData("src/../../secrets/id_rsa")]
    [InlineData("../project-evil/loot.txt")]
    [InlineData("..")]
    public void PathsThatEscapeTheRootAreRefused(string relative)
    {
        var result = _access.ReadFile("p", "r", relative, null);

        Assert.NotNull(result.Error);
        Assert.Null(result.Text);
        Assert.Null(result.Bytes);
    }

    [Fact]
    public void AbsolutePathOutsideTheRootIsRefused()
    {
        var result = _access.ReadFile("p", "r", Path.Combine(_outside, "id_rsa"), null);

        Assert.NotNull(result.Error);
        Assert.Null(result.Text);
    }

    [Fact]
    public void AbsolutePathInsideTheRootIsAllowed()
    {
        // Chat file-links carry absolute server paths, so these have to work — but only after the
        // same containment check.
        var result = _access.ReadFile("p", "r", Path.Combine(_root, "readme.md"), null);

        Assert.Null(result.Error);
        Assert.Equal("# hello", result.Text);
    }

    [Fact]
    public void ListingOutsideTheRootIsRefused()
    {
        var listing = _access.ListDirectory("p", "r", "../secrets");

        Assert.NotNull(listing.Error);
        Assert.Empty(listing.Entries);
    }

    // ---------------------------------------------------------------- listings

    [Fact]
    public void RootListingReturnsDirectoriesFirstThenFiles()
    {
        var listing = _access.ListDirectory("p", "r", "");

        Assert.Null(listing.Error);
        Assert.Equal("src", listing.Entries[0].Name);
        Assert.True(listing.Entries[0].IsDirectory);
        Assert.Contains(listing.Entries, e => e.Name == "readme.md" && !e.IsDirectory);
    }

    [Fact]
    public void ListingUsesForwardSlashRelativePaths()
    {
        var listing = _access.ListDirectory("p", "r", "src");

        var entry = Assert.Single(listing.Entries);
        Assert.Equal("src/app.cs", entry.RelativePath);
    }

    [Fact]
    public void MissingDirectoryReportsAnError()
    {
        var listing = _access.ListDirectory("p", "r", "nope");

        Assert.NotNull(listing.Error);
        Assert.Empty(listing.Entries);
    }

    // ---------------------------------------------------------------- versions and caching

    [Fact]
    public void KnownCurrentVersionIsAnsweredWithoutABody()
    {
        var first = _access.ReadFile("p", "r1", "readme.md", null);
        Assert.Equal("# hello", first.Text);

        // The whole point of the version token: the client already holds these bytes, so the
        // server should answer with the version alone rather than resending the file.
        var second = _access.ReadFile("p", "r2", "readme.md", first.Version);

        Assert.Null(second.Error);
        Assert.Null(second.Text);
        Assert.Equal(first.Version, second.Version);
    }

    [Fact]
    public void EditingAFileChangesItsVersion()
    {
        var before = _access.ReadFile("p", "r1", "readme.md", null).Version;

        // Size change alone is enough; mtime granularity makes a same-length rewrite unreliable
        // to assert on, and this is the case that matters (the agent rewriting a file).
        File.WriteAllText(Path.Combine(_root, "readme.md"), "# hello, world");

        var after = _access.ReadFile("p", "r2", "readme.md", null).Version;

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void StaleVersionCausesTheBodyToBeResent()
    {
        var first = _access.ReadFile("p", "r1", "readme.md", null);
        File.WriteAllText(Path.Combine(_root, "readme.md"), "# changed on the server");

        var second = _access.ReadFile("p", "r2", "readme.md", first.Version);

        Assert.Equal("# changed on the server", second.Text);
    }

    [Fact]
    public void MissingFileReportsAnErrorRatherThanThrowing()
    {
        var result = _access.ReadFile("p", "r", "nope.txt", null);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void BinaryContentIsFlaggedRatherThanSent()
    {
        File.WriteAllBytes(Path.Combine(_root, "blob.bin"), [1, 0, 2, 0, 3]);

        var result = _access.ReadFile("p", "r", "blob.bin", null);

        Assert.True(result.IsBinary);
        Assert.Null(result.Text);
    }

    [Fact]
    public void ImagesAreSentAsBytes()
    {
        var bytes = new byte[] { 137, 80, 78, 71, 1, 2, 3 };
        File.WriteAllBytes(Path.Combine(_root, "logo.png"), bytes);

        var result = _access.ReadFile("p", "r", "logo.png", null);

        Assert.Equal(bytes, result.Bytes);
        Assert.False(result.IsBinary);
    }

    // ---------------------------------------------------------------- test doubles

    private sealed class NullLog : ILogService
    {
        public string? LogFilePath => null;
        public void Init(string? logsFolder = null, string? sessionContext = null) { }
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message) { }
        public void Error(string message, Exception ex) { }
    }

    private sealed class NullPerf : IPerfDiagnostics
    {
        public bool Enabled { get; set; }
        public int LiveSessions => 0;
        public int WorkingSessions => 0;
        public void SessionCreated() { }
        public void SessionDisposed() { }
        public void WorkingSessionDelta(int delta) { }
        public void NoteWorkspaceDirty() { }
        public void MarkdownBuildDelta(int delta) { }
        public void GitOpStart() { }
        public void GitOpEnd(string command, double ms, int threadId) { }
        public void Start() { }
        public void Stop() { }
        public void LogEnvironment() { }
        public void LogHealth() { }
        public IDisposable Time(string name, double thresholdMs = 50) => new Scope();

        private sealed class Scope : IDisposable
        {
            public void Dispose() { }
        }
    }
}
