using PiCompanion.Application.Skills;

namespace PiCompanion.Core.Tests;

public sealed class SkillDiscoveryServiceTests
{
    [Fact]
    public void Discover_FindsGlobalAgentSkillAndReportsItsOrigin()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            WriteSkill(
                Path.Combine(root, ".agents", "skills", "find-skills", "SKILL.md"),
                "find-skills",
                "Finds installable Agent skills.");

            var snapshot = new SkillDiscoveryService(root).Discover();

            var skill = Assert.Single(snapshot.Skills);
            Assert.Equal("find-skills", skill.Name);
            Assert.True(skill.IsAvailable);
            Assert.True(skill.IsGloballyEffective);
            var origin = Assert.Single(skill.Origins);
            Assert.Equal("global", origin.Scope);
            Assert.Equal("agents", origin.Source);
            Assert.Null(origin.WorkspaceId);
            Assert.Contains(snapshot.Locations, location =>
                location.Scope == "global" &&
                location.Source == "pi" &&
                location.Status == "missing");
            Assert.Contains(snapshot.Locations, location =>
                location.Scope == "global" &&
                location.Source == "agents" &&
                location.Status == "loaded" &&
                location.SkillCount == 1);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Discover_MatchesPiRootRecursionIgnoreAndStopRules()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var piRoot = Path.Combine(root, ".pi", "agent", "skills");
            var agentsRoot = Path.Combine(root, ".agents", "skills");
            WriteSkill(Path.Combine(piRoot, "root.md"), "root-direct", "Pi root markdown skill.");
            WriteSkill(Path.Combine(piRoot, "nested", "SKILL.md"), "nested", "Nested directory skill.");
            WriteSkill(Path.Combine(piRoot, "nested", "deeper", "SKILL.md"), "too-deep", "Must not be reached.");
            WriteSkill(Path.Combine(piRoot, ".hidden", "SKILL.md"), "hidden", "Must not be reached.");
            WriteSkill(Path.Combine(piRoot, "node_modules", "dep", "SKILL.md"), "dependency", "Must not be reached.");
            WriteSkill(Path.Combine(piRoot, "ignored", "SKILL.md"), "ignored", "Must not be reached.");
            File.WriteAllText(Path.Combine(piRoot, ".gitignore"), "ignored/\n");

            WriteSkill(Path.Combine(agentsRoot, "direct.md"), "agents-direct", "Must be ignored.");
            WriteSkill(Path.Combine(agentsRoot, "directory", "SKILL.md"), "agents-directory", "Agent directory skill.");

            var snapshot = new SkillDiscoveryService(root).Discover();

            Assert.Equal(
                ["agents-directory", "nested", "root-direct"],
                snapshot.Skills.Select(skill => skill.Name).Order().ToArray());
            Assert.DoesNotContain(snapshot.Skills, skill =>
                skill.Name is "too-deep" or "hidden" or "dependency" or "ignored" or "agents-direct");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Discover_ReportsInvalidFrontmatterMissingDescriptionAndValidationWarnings()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var skillsRoot = Path.Combine(root, ".agents", "skills");
            WriteFile(
                Path.Combine(skillsRoot, "missing", "SKILL.md"),
                "---\nname: missing-description\n---\n# Missing");
            WriteFile(
                Path.Combine(skillsRoot, "broken", "SKILL.md"),
                "---\nname: \"broken\ndescription: Broken YAML\n---\n# Broken");
            WriteSkill(
                Path.Combine(skillsRoot, "warning", "SKILL.md"),
                "Invalid_Name",
                new string('x', 1025),
                disableModelInvocation: true);

            var snapshot = new SkillDiscoveryService(root).Discover();

            var missing = Assert.Single(snapshot.Skills, skill => skill.Name == "missing-description");
            Assert.False(missing.IsAvailable);
            Assert.Contains(missing.Diagnostics, diagnostic => diagnostic.Code == "description-required");

            var broken = Assert.Single(snapshot.Skills, skill => skill.Name == "broken");
            Assert.False(broken.IsAvailable);
            Assert.Contains(broken.Diagnostics, diagnostic => diagnostic.Code == "frontmatter-invalid");

            var warning = Assert.Single(snapshot.Skills, skill => skill.Name == "Invalid_Name");
            Assert.True(warning.IsAvailable);
            Assert.True(warning.DisableModelInvocation);
            Assert.Contains(warning.Diagnostics, diagnostic => diagnostic.Code == "name-invalid");
            Assert.Contains(warning.Diagnostics, diagnostic => diagnostic.Code == "description-too-long");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Discover_UsesProjectPiThenAncestorAgentsThenGlobalPrecedence()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            WriteSkill(
                Path.Combine(root, ".pi", "agent", "skills", "global-pi", "SKILL.md"),
                "shared",
                "Global Pi winner.");
            WriteSkill(
                Path.Combine(root, ".agents", "skills", "global-agents", "SKILL.md"),
                "shared",
                "Global Agent loser.");

            var workspacePath = Directory.CreateDirectory(Path.Combine(root, "repo", "app")).FullName;
            Directory.CreateDirectory(Path.Combine(root, "repo", ".git"));
            WriteSkill(
                Path.Combine(workspacePath, ".pi", "skills", "project", "SKILL.md"),
                "shared",
                "Project winner.");
            var workspace = new SkillDiscoveryWorkspace(Guid.NewGuid(), "App", workspacePath);

            var snapshot = new SkillDiscoveryService(root).Discover([workspace]);

            var project = Assert.Single(snapshot.Skills, skill =>
                skill.Description == "Project winner.");
            Assert.Contains(workspace.Id, project.EffectiveWorkspaceIds);
            Assert.False(project.IsGloballyEffective);

            var globalPi = Assert.Single(snapshot.Skills, skill =>
                skill.Description == "Global Pi winner.");
            Assert.True(globalPi.IsGloballyEffective);
            Assert.DoesNotContain(workspace.Id, globalPi.EffectiveWorkspaceIds);
            Assert.Contains(globalPi.Diagnostics, diagnostic =>
                diagnostic.Code == "name-collision" &&
                diagnostic.WorkspaceId == workspace.Id &&
                diagnostic.WinnerPath == project.FilePath);

            var globalAgents = Assert.Single(snapshot.Skills, skill =>
                skill.Description == "Global Agent loser.");
            Assert.False(globalAgents.IsGloballyEffective);
            Assert.Contains(globalAgents.Diagnostics, diagnostic =>
                diagnostic.Code == "name-collision" &&
                diagnostic.WorkspaceId is null &&
                diagnostic.WinnerPath == globalPi.FilePath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Discover_DoesNotActivateProjectSkillsUntilWorkspaceIsTrusted()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            WriteSkill(
                Path.Combine(root, ".pi", "agent", "skills", "global", "SKILL.md"),
                "shared",
                "Global fallback.");
            var workspacePath = Directory.CreateDirectory(Path.Combine(root, "repo")).FullName;
            WriteSkill(
                Path.Combine(workspacePath, ".pi", "skills", "local", "SKILL.md"),
                "shared",
                "Untrusted project skill.");
            var workspace = new SkillDiscoveryWorkspace(
                Guid.NewGuid(),
                "Repo",
                workspacePath,
                TrustStatus: "undecided");

            var snapshot = new SkillDiscoveryService(root).Discover([workspace]);

            var project = Assert.Single(snapshot.Skills, skill =>
                skill.Description == "Untrusted project skill.");
            Assert.DoesNotContain(workspace.Id, project.EffectiveWorkspaceIds);
            Assert.Contains(project.Diagnostics, diagnostic =>
                diagnostic.Code == "workspace-untrusted" &&
                diagnostic.WorkspaceId == workspace.Id);

            var global = Assert.Single(snapshot.Skills, skill =>
                skill.Description == "Global fallback.");
            Assert.Contains(workspace.Id, global.EffectiveWorkspaceIds);
            var trust = Assert.Single(snapshot.WorkspaceTrust);
            Assert.Equal("undecided", trust.Status);
            Assert.Equal(workspace.Id, trust.WorkspaceId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Discover_InheritsAgentSkillsOnlyThroughGitRootAndMergesWorkspaceBindings()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var repository = Directory.CreateDirectory(Path.Combine(root, "parent", "repo")).FullName;
            Directory.CreateDirectory(Path.Combine(repository, ".git"));
            var firstWorkspace = Directory.CreateDirectory(Path.Combine(repository, "apps", "first")).FullName;
            var secondWorkspace = Directory.CreateDirectory(Path.Combine(repository, "apps", "second")).FullName;
            WriteSkill(
                Path.Combine(repository, ".agents", "skills", "shared-repo", "SKILL.md"),
                "shared-repo",
                "Inherited by both registered workspaces.");
            WriteSkill(
                Path.Combine(root, "parent", ".agents", "skills", "outside", "SKILL.md"),
                "outside",
                "Must not cross the Git root.");

            var first = new SkillDiscoveryWorkspace(Guid.NewGuid(), "First", firstWorkspace);
            var second = new SkillDiscoveryWorkspace(Guid.NewGuid(), "Second", secondWorkspace);
            var snapshot = new SkillDiscoveryService(root).Discover([first, second]);

            var skill = Assert.Single(snapshot.Skills);
            Assert.Equal("shared-repo", skill.Name);
            Assert.Equal(2, skill.Origins.Count);
            Assert.All(skill.Origins, origin =>
            {
                Assert.Equal("workspace", origin.Scope);
                Assert.Equal("agents", origin.Source);
                Assert.True(origin.Inherited);
            });
            Assert.Equal(
                new[] { first.Id, second.Id }.Order().ToArray(),
                skill.EffectiveWorkspaceIds.Order().ToArray());
            Assert.DoesNotContain(snapshot.Skills, candidate => candidate.Name == "outside");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Discover_RefreshesRegisteredWorkspaceWithoutCachingStaleResults()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var workspacePath = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName;
            Directory.CreateDirectory(Path.Combine(workspacePath, ".git"));
            var workspace = new SkillDiscoveryWorkspace(Guid.NewGuid(), "Workspace", workspacePath);
            var service = new SkillDiscoveryService(root);

            var empty = service.Discover([workspace]);
            Assert.Empty(empty.Skills);
            Assert.Contains(empty.Locations, location =>
                location.WorkspaceId == workspace.Id &&
                location.Source == "pi" &&
                location.Status == "missing");

            WriteSkill(
                Path.Combine(workspacePath, ".pi", "skills", "fresh", "SKILL.md"),
                "fresh",
                "Created after the first scan.");
            var refreshed = service.Discover([workspace]);

            var skill = Assert.Single(refreshed.Skills);
            Assert.Equal("fresh", skill.Name);
            var origin = Assert.Single(skill.Origins);
            Assert.Equal(workspace.Id, origin.WorkspaceId);
            Assert.Contains(workspace.Id, skill.EffectiveWorkspaceIds);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Discover_AlwaysScansBothNativeSources()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            WriteSkill(
                Path.Combine(root, ".pi", "agent", "skills", "pi", "SKILL.md"),
                "pi-only",
                "Pi-only skill.");
            WriteSkill(
                Path.Combine(root, ".agents", "skills", "agents", "SKILL.md"),
                "agents-only",
                "Shared Agent skill.");

            var snapshot = new SkillDiscoveryService(root).Discover();

            Assert.Equal(
                ["agents-only", "pi-only"],
                snapshot.Skills.Select(skill => skill.Name).Order().ToArray());
            Assert.Contains(snapshot.Locations, location =>
                location.Source == "pi" && location.Status == "loaded");
            Assert.Contains(snapshot.Locations, location =>
                location.Source == "agents" && location.Status == "loaded");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Discover_FingerprintsEachPhysicalCopyAndReadsFrontmatterMetadata()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var piSkill = Path.Combine(root, ".pi", "agent", "skills", "shared");
            var agentsSkill = Path.Combine(root, ".agents", "skills", "shared");
            var content =
                "---\nname: shared\ndescription: Shared skill.\nversion: 1.4.0\nlicense: MIT\nauthor: Example\n---\n# shared";
            WriteFile(Path.Combine(piSkill, "SKILL.md"), content);
            WriteFile(Path.Combine(piSkill, "reference.txt"), "same reference");
            WriteFile(Path.Combine(agentsSkill, "SKILL.md"), content);
            WriteFile(Path.Combine(agentsSkill, "reference.txt"), "same reference");

            var service = new SkillDiscoveryService(root);
            var identical = service.Discover().Skills
                .Where(skill => skill.Name == "shared")
                .ToArray();

            Assert.Equal(2, identical.Length);
            Assert.Single(identical.Select(skill => skill.ContentHash).Distinct());
            Assert.All(identical, skill =>
            {
                Assert.Equal("1.4.0", skill.Version);
                Assert.Equal("MIT", skill.License);
                Assert.Equal("Example", skill.Metadata["author"]);
                Assert.Equal(2, skill.FileCount);
                Assert.True(skill.TotalSize > 0);
                Assert.NotNull(skill.LastModifiedAt);
            });

            WriteFile(Path.Combine(agentsSkill, "reference.txt"), "different reference");
            var changed = service.Discover().Skills
                .Where(skill => skill.Name == "shared")
                .ToArray();

            Assert.Equal(2, changed.Select(skill => skill.ContentHash).Distinct().Count());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Discover_MergesSkillsCliCompatibilityLinkWithPhysicalAgentInstallation()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var agentsSkill = Path.Combine(root, ".agents", "skills", "shared");
            var piSkill = Path.Combine(root, ".pi", "agent", "skills", "shared");
            WriteSkill(
                Path.Combine(agentsSkill, "SKILL.md"),
                "shared",
                "Installed once and exposed to Pi through a compatibility link.");
            WriteFile(Path.Combine(agentsSkill, "reference.txt"), "reference");
            Directory.CreateDirectory(Path.GetDirectoryName(piSkill)!);
            if (!TryCreateDirectoryLink(piSkill, agentsSkill))
            {
                return;
            }

            var skill = Assert.Single(new SkillDiscoveryService(root).Discover().Skills);

            Assert.Equal(Path.GetFullPath(agentsSkill), skill.InstallPath);
            Assert.NotNull(skill.ContentHash);
            Assert.Equal(2, skill.FileCount);
            Assert.True(skill.TotalSize > 0);
            Assert.DoesNotContain(
                skill.Diagnostics,
                diagnostic => diagnostic.Code == "content-inspection-failed");
            Assert.Equal(2, skill.Origins.Count);

            var agentsOrigin = Assert.Single(skill.Origins, origin => origin.Source == "agents");
            Assert.False(agentsOrigin.IsCompatibilityLink);
            Assert.Equal(Path.GetFullPath(agentsSkill), agentsOrigin.InstallPath);
            Assert.Null(agentsOrigin.LinkTarget);

            var piOrigin = Assert.Single(skill.Origins, origin => origin.Source == "pi");
            Assert.True(piOrigin.IsCompatibilityLink);
            Assert.Equal(Path.GetFullPath(piSkill), piOrigin.InstallPath);
            Assert.Equal(Path.GetFullPath(agentsSkill), piOrigin.LinkTarget);
            Assert.False(SkillRemovalService.CanRemove(skill, out var reason));
            Assert.Contains("兼容链接", reason);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Discover_DoesNotTrustArbitraryPiDirectoryLinks()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var externalSkill = Path.Combine(root, "external", "shared");
            var piSkill = Path.Combine(root, ".pi", "agent", "skills", "shared");
            WriteSkill(
                Path.Combine(externalSkill, "SKILL.md"),
                "shared",
                "An arbitrary linked skill.");
            Directory.CreateDirectory(Path.GetDirectoryName(piSkill)!);
            if (!TryCreateDirectoryLink(piSkill, externalSkill))
            {
                return;
            }

            var skill = Assert.Single(new SkillDiscoveryService(root).Discover().Skills);
            var origin = Assert.Single(skill.Origins);

            Assert.False(origin.IsCompatibilityLink);
            Assert.Null(origin.LinkTarget);
            Assert.Null(skill.ContentHash);
            Assert.Contains(
                skill.Diagnostics,
                diagnostic => diagnostic.Code == "content-inspection-failed");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Discover_RecognizesWorkspaceSkillsCliCompatibilityLink()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var workspacePath = Directory.CreateDirectory(Path.Combine(root, "repo")).FullName;
            Directory.CreateDirectory(Path.Combine(workspacePath, ".git"));
            var agentsSkill = Path.Combine(workspacePath, ".agents", "skills", "shared");
            var piSkill = Path.Combine(workspacePath, ".pi", "skills", "shared");
            WriteSkill(
                Path.Combine(agentsSkill, "SKILL.md"),
                "shared",
                "Workspace compatibility link.");
            Directory.CreateDirectory(Path.GetDirectoryName(piSkill)!);
            if (!TryCreateDirectoryLink(piSkill, agentsSkill))
            {
                return;
            }

            var workspace = new SkillDiscoveryWorkspace(
                Guid.NewGuid(),
                "Repo",
                workspacePath);
            var skill = Assert.Single(
                new SkillDiscoveryService(root).Discover([workspace]).Skills);

            Assert.Equal(Path.GetFullPath(agentsSkill), skill.InstallPath);
            Assert.Contains(workspace.Id, skill.EffectiveWorkspaceIds);
            Assert.Equal(2, skill.Origins.Count);
            Assert.All(skill.Origins, origin => Assert.Equal(workspace.Id, origin.WorkspaceId));
            var piOrigin = Assert.Single(skill.Origins, origin => origin.Source == "pi");
            Assert.True(piOrigin.IsCompatibilityLink);
            Assert.Equal(Path.GetFullPath(piSkill), piOrigin.InstallPath);
            Assert.Equal(Path.GetFullPath(agentsSkill), piOrigin.LinkTarget);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pi-companion-skills-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WriteSkill(
        string path,
        string name,
        string description,
        bool disableModelInvocation = false)
    {
        WriteFile(
            path,
            $"---\nname: {name}\ndescription: {description}\ndisable-model-invocation: {disableModelInvocation.ToString().ToLowerInvariant()}\n---\n# {name}");
    }

    private static void WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static bool TryCreateDirectoryLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
