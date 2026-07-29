using PiCompanion.Application.Skills;

namespace PiCompanion.Core.Tests;

public sealed class SkillRemovalServiceTests
{
    [Fact]
    public void Remove_MovesPiSkillDirectoryToHiddenRecoveryArea()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var skillDirectory = Path.Combine(root, ".pi", "agent", "skills", "sample");
            WriteSkill(Path.Combine(skillDirectory, "SKILL.md"), "sample");
            File.WriteAllText(Path.Combine(skillDirectory, "reference.txt"), "reference");
            var discovery = new SkillDiscoveryService(root);
            var skill = Assert.Single(discovery.Discover().Skills);
            Assert.True(SkillRemovalService.CanRemove(skill, out var reason), reason);

            var result = new SkillRemovalService().Remove(skill, skill.ContentHash!);

            Assert.False(Directory.Exists(skillDirectory));
            Assert.True(Directory.Exists(result.RecoveryPath));
            Assert.True(File.Exists(Path.Combine(result.RecoveryPath, "removal.json")));
            Assert.True(File.Exists(Path.Combine(result.RecoveryPath, "sample", "SKILL.md")));
            Assert.Empty(discovery.Discover().Skills);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Remove_RejectsContentChangedAfterDiscovery()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var skillPath = Path.Combine(root, ".pi", "agent", "skills", "sample", "SKILL.md");
            WriteSkill(skillPath, "sample");
            var skill = Assert.Single(new SkillDiscoveryService(root).Discover().Skills);
            File.AppendAllText(skillPath, "\nChanged after discovery.");

            var exception = Assert.Throws<InvalidOperationException>(() =>
                new SkillRemovalService().Remove(skill, skill.ContentHash!));

            Assert.Contains("发生了变化", exception.Message);
            Assert.True(File.Exists(skillPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Remove_AllowsRootMarkdownWithoutDeletingThePiRoot()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var piRoot = Path.Combine(root, ".pi", "agent", "skills");
            var skillPath = Path.Combine(piRoot, "standalone.md");
            WriteSkill(skillPath, "standalone");
            File.WriteAllText(Path.Combine(piRoot, "keep.txt"), "keep");
            var skill = Assert.Single(new SkillDiscoveryService(root).Discover().Skills);

            new SkillRemovalService().Remove(skill, skill.ContentHash!);

            Assert.True(Directory.Exists(piRoot));
            Assert.False(File.Exists(skillPath));
            Assert.True(File.Exists(Path.Combine(piRoot, "keep.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CanRemove_RejectsAgentNativeLocation()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            WriteSkill(
                Path.Combine(root, ".agents", "skills", "sample", "SKILL.md"),
                "sample");
            var skill = Assert.Single(new SkillDiscoveryService(root).Discover().Skills);

            Assert.False(SkillRemovalService.CanRemove(skill, out var reason));
            Assert.Contains("Pi 专属", reason);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pi-companion-removal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WriteSkill(string path, string name)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(
            path,
            $"---\nname: {name}\ndescription: Test skill.\nversion: 1.0.0\nlicense: MIT\n---\n# {name}");
    }
}
