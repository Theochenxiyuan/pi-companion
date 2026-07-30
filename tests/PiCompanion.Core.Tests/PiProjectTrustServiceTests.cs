using System.Text.Json;
using PiCompanion.Application.Skills;

namespace PiCompanion.Core.Tests;

public sealed class PiProjectTrustServiceTests
{
    [Fact]
    public void Trust_PersistsPiCompatibleDecisionAndReportsInheritance()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var profile = Directory.CreateDirectory(Path.Combine(root, "profile")).FullName;
            var parent = Directory.CreateDirectory(Path.Combine(root, "projects")).FullName;
            var workspace = Directory.CreateDirectory(Path.Combine(parent, "child")).FullName;
            var service = new PiProjectTrustService(profile);

            var initial = service.GetStatus(workspace);
            service.Trust(parent);
            var inherited = service.GetStatus(workspace);

            Assert.Equal("undecided", initial.Status);
            Assert.Equal("trusted", inherited.Status);
            Assert.True(inherited.Inherited);
            Assert.Equal(parent, inherited.DecisionPath);
            using var document = JsonDocument.Parse(File.ReadAllText(inherited.TrustStorePath));
            Assert.True(document.RootElement.GetProperty(parent).GetBoolean());

            var exact = service.Trust(workspace);
            Assert.Equal("trusted", exact.Status);
            Assert.False(exact.Inherited);
            Assert.Equal(workspace, exact.DecisionPath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Trust_OverridesDeclinedAncestorWithoutRemovingOtherDecisions()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var profile = Directory.CreateDirectory(Path.Combine(root, "profile")).FullName;
            var parent = Directory.CreateDirectory(Path.Combine(root, "projects")).FullName;
            var workspace = Directory.CreateDirectory(Path.Combine(parent, "child")).FullName;
            var unrelated = Directory.CreateDirectory(Path.Combine(root, "unrelated")).FullName;
            var trustPath = Path.Combine(profile, ".pi", "agent", "trust.json");
            Directory.CreateDirectory(Path.GetDirectoryName(trustPath)!);
            File.WriteAllText(
                trustPath,
                JsonSerializer.Serialize(new Dictionary<string, bool?>
                {
                    [parent] = false,
                    [unrelated] = true,
                }));
            var service = new PiProjectTrustService(profile);

            Assert.Equal("declined", service.GetStatus(workspace).Status);
            service.Trust(workspace);

            Assert.Equal("trusted", service.GetStatus(workspace).Status);
            using var document = JsonDocument.Parse(File.ReadAllText(trustPath));
            Assert.False(document.RootElement.GetProperty(parent).GetBoolean());
            Assert.True(document.RootElement.GetProperty(workspace).GetBoolean());
            Assert.True(document.RootElement.GetProperty(unrelated).GetBoolean());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SetDecision_PersistsAnExplicitDecline()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var profile = Directory.CreateDirectory(Path.Combine(root, "profile")).FullName;
            var workspace = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName;
            var service = new PiProjectTrustService(profile);

            var declined = service.SetDecision(workspace, trusted: false);

            Assert.Equal("declined", declined.Status);
            Assert.False(declined.Inherited);
            Assert.Equal(workspace, declined.DecisionPath);
            using var document = JsonDocument.Parse(File.ReadAllText(declined.TrustStorePath));
            Assert.False(document.RootElement.GetProperty(workspace).GetBoolean());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pi-companion-trust-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
