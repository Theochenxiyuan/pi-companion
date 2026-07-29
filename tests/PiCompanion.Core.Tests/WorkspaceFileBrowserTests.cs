using PiCompanion.Application.Files;

namespace PiCompanion.Core.Tests;

public sealed class WorkspaceFileBrowserTests
{
    [Fact]
    public void ReadDirectory_ReturnsOnlyImmediateChildrenWithFoldersFirst()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "z-folder"));
            var nested = Directory.CreateDirectory(Path.Combine(root, "a-folder"));
            File.WriteAllText(Path.Combine(nested.FullName, "nested.txt"), "nested");
            File.WriteAllText(Path.Combine(root, "zeta.txt"), "zeta");
            File.WriteAllText(Path.Combine(root, ".hidden"), "hidden");
            var browser = new WorkspaceFileBrowser();

            var listing = browser.ReadDirectory(root, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(
                ["a-folder", "z-folder", ".hidden", "zeta.txt"],
                listing.Entries.Select(entry => entry.Name));
            Assert.True(listing.Entries[0].IsDirectory);
            Assert.True(listing.Entries[0].HasChildren);
            Assert.DoesNotContain(listing.Entries, entry => entry.RelativePath.Contains("nested.txt", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Search_FindsNestedFilesAndReportsWorkspaceRelativePaths()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var nested = Directory.CreateDirectory(Path.Combine(root, "src", "feature"));
            File.WriteAllText(Path.Combine(nested.FullName, "WorkspaceTarget.cs"), "target");
            var browser = new WorkspaceFileBrowser();

            var result = browser.Search(root, "target", TestContext.Current.CancellationToken);

            var match = Assert.Single(result.Entries);
            Assert.Equal("WorkspaceTarget.cs", match.Name);
            Assert.Equal("src/feature/WorkspaceTarget.cs", match.RelativePath);
            Assert.False(result.Truncated);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void IgnoreMetadata_DecoratesListingsAndSearchSkipsIgnoredTreesUnlessRequested()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, ".gitignore"), "dist/\n");
            File.WriteAllText(Path.Combine(root, ".ignore"), "logs/\n");
            File.WriteAllText(Path.Combine(root, ".fdignore"), "*.tmp\n");
            var nodeModules = Directory.CreateDirectory(Path.Combine(root, "node_modules"));
            var dist = Directory.CreateDirectory(Path.Combine(root, "dist"));
            var logs = Directory.CreateDirectory(Path.Combine(root, "logs"));
            var source = Directory.CreateDirectory(Path.Combine(root, "src"));
            File.WriteAllText(Path.Combine(root, "scratch.tmp"), "ignored");
            File.WriteAllText(Path.Combine(nodeModules.FullName, "DependencyTarget.js"), "ignored");
            File.WriteAllText(Path.Combine(dist.FullName, "BundleTarget.js"), "ignored");
            File.WriteAllText(Path.Combine(logs.FullName, "LogTarget.txt"), "ignored");
            File.WriteAllText(Path.Combine(source.FullName, "SourceTarget.cs"), "visible");
            var browser = new WorkspaceFileBrowser();

            var listing = browser.ReadDirectory(root, cancellationToken: TestContext.Current.CancellationToken);

            AssertIgnore(listing, "node_modules", "built-in");
            AssertIgnore(listing, "dist", ".gitignore");
            AssertIgnore(listing, "logs", ".ignore");
            AssertIgnore(listing, "scratch.tmp", ".fdignore");
            Assert.False(Assert.Single(listing.Entries, entry => entry.Name == "src").IsIgnored);

            var defaultSearch = browser.Search(root, "target", TestContext.Current.CancellationToken);
            var visible = Assert.Single(defaultSearch.Entries);
            Assert.Equal("src/SourceTarget.cs", visible.RelativePath);
            Assert.False(visible.IsIgnored);

            var inclusiveSearch = browser.Search(
                root,
                "target",
                TestContext.Current.CancellationToken,
                includeIgnored: true);
            Assert.Equal(4, inclusiveSearch.Entries.Count);
            Assert.Contains(inclusiveSearch.Entries, entry =>
                entry.RelativePath == "node_modules/DependencyTarget.js" &&
                entry.IsIgnored &&
                entry.IgnoreSource == "built-in");
            Assert.Contains(inclusiveSearch.Entries, entry =>
                entry.RelativePath == "dist/BundleTarget.js" &&
                entry.IsIgnored &&
                entry.IgnoreSource == ".gitignore");
            Assert.Contains(inclusiveSearch.Entries, entry =>
                entry.RelativePath == "logs/LogTarget.txt" &&
                entry.IsIgnored &&
                entry.IgnoreSource == ".ignore");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }

        static void AssertIgnore(WorkspaceDirectoryListing listing, string name, string source)
        {
            var entry = Assert.Single(listing.Entries, candidate => candidate.Name == name);
            Assert.True(entry.IsIgnored);
            Assert.Equal(source, entry.IgnoreSource);
        }
    }

    [Fact]
    public void ResolveExistingPath_RejectsTraversalOutsideTheWorkspace()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var browser = new WorkspaceFileBrowser();

            Assert.Throws<InvalidOperationException>(() => browser.ResolveExistingPath(root, "../outside.txt"));
            if (OperatingSystem.IsWindows())
            {
                Assert.Throws<InvalidOperationException>(() => browser.ResolveExistingPath(root, "nul"));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "PiCompanionFileBrowserTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
