using PiCompanion.Application.PiRpc;

namespace PiCompanion.Core.Tests;

public sealed class PiRuntimeResolverTests
{
    [Fact]
    public void Resolve_UsesExplicitJavaScriptRuntimeAndConfiguredNode()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var runtime = Path.Combine(root, "cli.js");
            File.WriteAllText(runtime, string.Empty);
            var resolver = new PiRuntimeResolver(runtime, root, "node.exe");

            var command = resolver.Resolve();

            Assert.Equal("node.exe", command.FileName);
            Assert.Equal(runtime, Assert.Single(command.PrefixArguments));
            Assert.Equal(runtime, command.RuntimePath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Resolve_PrefersApplicationPrivateExecutable()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var privateDirectory = Path.Combine(root, "PiRuntime");
            Directory.CreateDirectory(privateDirectory);
            var runtime = Path.Combine(privateDirectory, "pi.exe");
            File.WriteAllText(runtime, string.Empty);

            var command = new PiRuntimeResolver(baseDirectory: root).Resolve();

            Assert.Equal(runtime, command.FileName);
            Assert.Empty(command.PrefixArguments);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Resolve_DoesNotFallBackToGlobalPi()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var missing = Path.Combine(root, "missing", "cli.js");
            var exception = Assert.Throws<FileNotFoundException>(() =>
                new PiRuntimeResolver(missing, root, "node.exe").Resolve());

            Assert.Equal(missing, exception.FileName);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Resolve_PrefersCurrentEarendilPackageScopeOverLegacyScope()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var privateRoot = Path.Combine(root, "PiRuntime");
            var currentRuntime = Path.Combine(privateRoot, "node_modules", "@earendil-works", "pi-coding-agent", "dist", "cli.js");
            var legacyRuntime = Path.Combine(privateRoot, "node_modules", "@mariozechner", "pi-coding-agent", "dist", "cli.js");
            Directory.CreateDirectory(Path.GetDirectoryName(currentRuntime)!);
            Directory.CreateDirectory(Path.GetDirectoryName(legacyRuntime)!);
            File.WriteAllText(currentRuntime, string.Empty);
            File.WriteAllText(legacyRuntime, string.Empty);
            File.WriteAllText(Path.Combine(privateRoot, "node.exe"), string.Empty);

            var command = new PiRuntimeResolver(baseDirectory: root).Resolve();

            Assert.Equal(currentRuntime, command.RuntimePath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Resolve_DevelopmentBuildUsesLocallyInstalledRuntime()
    {
        var root = CreateTemporaryDirectory();
        var npmRoot = CreateTemporaryDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, PiRuntimeResolver.DevelopmentMarkerFileName), string.Empty);
            var runtime = Path.Combine(npmRoot, "node_modules", "@earendil-works", "pi-coding-agent", "dist", "cli.js");
            Directory.CreateDirectory(Path.GetDirectoryName(runtime)!);
            File.WriteAllText(runtime, string.Empty);

            var command = new PiRuntimeResolver(
                baseDirectory: root,
                nodeExecutablePath: "node.exe",
                globalRuntimeRoots: [npmRoot]).Resolve();

            Assert.Equal("node.exe", command.FileName);
            Assert.Equal(runtime, command.RuntimePath);
            Assert.Equal(runtime, Assert.Single(command.PrefixArguments));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(npmRoot, recursive: true);
        }
    }

    [Fact]
    public void Resolve_DevelopmentBuildPrefersLocalRuntimeOverPrivateRuntime()
    {
        var root = CreateTemporaryDirectory();
        var npmRoot = CreateTemporaryDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, PiRuntimeResolver.DevelopmentMarkerFileName), string.Empty);
            var privateRuntime = Path.Combine(root, "PiRuntime", "pi.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(privateRuntime)!);
            File.WriteAllText(privateRuntime, string.Empty);
            var localRuntime = Path.Combine(npmRoot, "node_modules", "@earendil-works", "pi-coding-agent", "dist", "cli.js");
            Directory.CreateDirectory(Path.GetDirectoryName(localRuntime)!);
            File.WriteAllText(localRuntime, string.Empty);

            var command = new PiRuntimeResolver(
                baseDirectory: root,
                nodeExecutablePath: "node.exe",
                globalRuntimeRoots: [npmRoot]).Resolve();

            Assert.Equal(localRuntime, command.RuntimePath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(npmRoot, recursive: true);
        }
    }

    [Fact]
    public void Resolve_FormalBuildDoesNotUseLocallyInstalledRuntime()
    {
        var root = CreateTemporaryDirectory();
        var npmRoot = CreateTemporaryDirectory();
        try
        {
            var runtime = Path.Combine(npmRoot, "node_modules", "@earendil-works", "pi-coding-agent", "dist", "cli.js");
            Directory.CreateDirectory(Path.GetDirectoryName(runtime)!);
            File.WriteAllText(runtime, string.Empty);

            Assert.Throws<FileNotFoundException>(() => new PiRuntimeResolver(
                baseDirectory: root,
                nodeExecutablePath: "node.exe",
                globalRuntimeRoots: [npmRoot]).Resolve());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(npmRoot, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "PiCompanionTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
