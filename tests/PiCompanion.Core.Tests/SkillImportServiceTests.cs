using System.IO.Compression;
using PiCompanion.Application.Skills;

namespace PiCompanion.Core.Tests;

public sealed class SkillImportServiceTests
{
    [Fact]
    public void InspectDirectory_ListsEveryFileBeforePreparingAnyPiTarget()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var source = Path.Combine(root, "source", "sample");
            WriteSkill(Path.Combine(source, "SKILL.md"), "sample");
            Directory.CreateDirectory(Path.Combine(source, "scripts"));
            File.WriteAllText(Path.Combine(source, "scripts", "run.ps1"), "Write-Output ok");
            File.WriteAllText(Path.Combine(source, "reference.txt"), "reference");
            var previewRoot = Path.Combine(root, "preview");
            var service = new SkillImportService(root, previewRoot);

            var inspection = service.InspectDirectory(source);

            Assert.Equal(3, inspection.FileCount);
            Assert.Equal(
                ["SKILL.md", "reference.txt", "scripts/run.ps1"],
                inspection.Files.Select(static file => file.RelativePath)
                    .OrderBy(static path => path, StringComparer.Ordinal));
            Assert.Equal(["scripts/run.ps1"], inspection.ScriptFiles);
            Assert.False(Directory.Exists(Path.Combine(root, ".pi")));

            var preparation = service.PrepareSource(
                inspection.Token,
                "global",
                null,
                null);
            Assert.Equal(inspection.Token, preparation.SourceToken);
            Assert.True(Directory.Exists(Path.Combine(root, ".pi", "agent", "skills")));
            Assert.False(Directory.Exists(preparation.TargetPath));

            service.CancelSource(inspection.Token);
            Assert.Empty(Directory.EnumerateDirectories(previewRoot));
            Assert.DoesNotContain(
                Directory.EnumerateDirectories(
                    Path.Combine(root, ".pi", "agent", "skills"),
                    ".pi-companion-import-*"),
                _ => true);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ImportDirectory_CommitsAtomicallyToGlobalPiDirectory()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var source = Path.Combine(root, "source", "sample");
            WriteSkill(Path.Combine(source, "SKILL.md"), "sample");
            File.WriteAllText(Path.Combine(source, "reference.txt"), "reference");
            var service = new SkillImportService(root);

            var preparation = service.PrepareDirectory(source, "global", null, null);
            var result = service.Commit(preparation.Token);

            Assert.False(preparation.RequiresConfirmation);
            Assert.Equal(
                Path.Combine(root, ".pi", "agent", "skills", "sample"),
                result.TargetPath);
            Assert.True(File.Exists(Path.Combine(result.TargetPath, "SKILL.md")));
            Assert.True(File.Exists(Path.Combine(
                result.TargetPath,
                SkillImportService.MarkerFileName)));
            Assert.DoesNotContain(
                Directory.EnumerateDirectories(
                    Path.GetDirectoryName(result.TargetPath)!,
                    ".pi-companion-import-*"),
                _ => true);
            var discovered = Assert.Single(new SkillDiscoveryService(root).Discover().Skills);
            Assert.Equal("sample", discovered.Name);
            Assert.True(discovered.IsGloballyEffective);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ImportDirectory_RequiresConfirmationForProjectTrustAndScripts()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var source = Path.Combine(root, "source", "scripted");
            WriteSkill(Path.Combine(source, "SKILL.md"), "scripted");
            Directory.CreateDirectory(Path.Combine(source, "scripts"));
            File.WriteAllText(Path.Combine(source, "scripts", "run.ps1"), "Write-Output ok");
            var workspacePath = Path.Combine(root, "workspace");
            Directory.CreateDirectory(workspacePath);
            var workspace = new SkillDiscoveryWorkspace(
                Guid.NewGuid(),
                "Workspace",
                workspacePath);
            var trust = new PiProjectTrustSnapshot(
                "undecided",
                workspacePath,
                null,
                false,
                Path.Combine(root, ".pi", "agent", "trust.json"));
            var service = new SkillImportService(root);

            var preparation = service.PrepareDirectory(
                source,
                "workspace",
                workspace,
                trust);

            Assert.True(preparation.RequiresConfirmation);
            Assert.True(preparation.RequiresProjectTrust);
            Assert.Equal(["scripts/run.ps1"], preparation.ScriptFiles);
            var afterMoveCalled = false;
            var result = service.Commit(preparation.Token, () => afterMoveCalled = true);
            Assert.True(afterMoveCalled);
            Assert.Equal(
                Path.Combine(workspacePath, ".pi", "skills", "scripted"),
                result.TargetPath);
            var discovered = Assert.Single(
                new SkillDiscoveryService(root).Discover([workspace]).Skills,
                skill => skill.Name == "scripted");
            Assert.Contains(workspace.Id, discovered.EffectiveWorkspaceIds);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ImportArchive_AllowsOneWrapperAndRejectsPathTraversal()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var validArchive = Path.Combine(root, "valid.zip");
            using (var archive = ZipFile.Open(validArchive, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("wrapper/sample/SKILL.md");
                using var writer = new StreamWriter(entry.Open());
                writer.Write(SkillText("sample"));
            }
            var service = new SkillImportService(root);
            var preparation = service.PrepareArchive(validArchive, "global", null, null);
            var result = service.Commit(preparation.Token);
            Assert.True(File.Exists(Path.Combine(result.TargetPath, "SKILL.md")));

            var unsafeArchive = Path.Combine(root, "unsafe.zip");
            using (var archive = ZipFile.Open(unsafeArchive, ZipArchiveMode.Create))
            {
                archive.CreateEntry("../outside.txt");
                var entry = archive.CreateEntry("SKILL.md");
                using var writer = new StreamWriter(entry.Open());
                writer.Write(SkillText("unsafe"));
            }

            var exception = Assert.Throws<SkillImportException>(() =>
                service.PrepareArchive(unsafeArchive, "global", null, null));
            Assert.Contains("不安全路径", exception.Message);
            Assert.False(File.Exists(Path.Combine(root, "outside.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ImportDirectory_NeverOverwritesAnExistingTarget()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var source = Path.Combine(root, "source", "sample");
            WriteSkill(Path.Combine(source, "SKILL.md"), "sample");
            var existing = Path.Combine(root, ".pi", "agent", "skills", "sample");
            Directory.CreateDirectory(existing);
            File.WriteAllText(Path.Combine(existing, "keep.txt"), "keep");
            var service = new SkillImportService(root);

            var exception = Assert.Throws<SkillImportException>(() =>
                service.PrepareDirectory(source, "global", null, null));

            Assert.Contains("已存在", exception.Message);
            Assert.Equal("keep", File.ReadAllText(Path.Combine(existing, "keep.txt")));
            Assert.False(File.Exists(Path.Combine(existing, "SKILL.md")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pi-companion-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WriteSkill(string path, string name)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, SkillText(name));
    }

    private static string SkillText(string name) =>
        $"---\nname: {name}\ndescription: Test skill.\nversion: 1.0.0\nlicense: MIT\n---\n# {name}";
}
