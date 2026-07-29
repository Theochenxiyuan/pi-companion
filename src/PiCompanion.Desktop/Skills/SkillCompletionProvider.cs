using System.IO;
using PiCompanion.Application.Skills;
using PiCompanion.Core.Tasks;
using PiCompanion.Desktop.Localization;

namespace PiCompanion.Desktop.Skills;

internal sealed record SkillCompletionItem(
    string Name,
    string Description,
    bool ManualOnly)
{
    public string KindLabel => DesktopLocalizer.Text("技能", "Skill");
}

internal sealed class SkillCompletionProvider
{
    private readonly SkillDiscoveryService _discovery;
    private readonly PiProjectTrustService _projectTrust;

    public SkillCompletionProvider(
        SkillDiscoveryService discovery,
        PiProjectTrustService? projectTrust = null)
    {
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _projectTrust = projectTrust ?? new PiProjectTrustService();
    }

    public Task<IReadOnlyList<SkillCompletionItem>> GetEffectiveSkillsAsync(
        string workingDirectory,
        TaskScopeKind scopeKind,
        Guid? contextId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        return Task.Run<IReadOnlyList<SkillCompletionItem>>(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var effectiveContextId = contextId ?? Guid.NewGuid();
                IReadOnlyList<SkillDiscoveryWorkspace> workspaces =
                    scopeKind == TaskScopeKind.Workspace
                        ? [new SkillDiscoveryWorkspace(
                            effectiveContextId,
                            Path.GetFileName(Path.TrimEndingDirectorySeparator(workingDirectory)),
                            workingDirectory,
                            _projectTrust.GetStatus(workingDirectory).Status)]
                        : [];
                var snapshot = _discovery.Discover(workspaces);
                cancellationToken.ThrowIfCancellationRequested();
                return snapshot.Skills
                    .Where(skill =>
                        skill.IsAvailable &&
                        (scopeKind == TaskScopeKind.GeneralChat
                            ? skill.IsGloballyEffective
                            : skill.EffectiveWorkspaceIds.Contains(effectiveContextId)))
                    .Select(skill => new SkillCompletionItem(
                        skill.Name,
                        skill.Description ?? DesktopLocalizer.Text(
                            "没有提供技能描述",
                            "No skill description provided"),
                        skill.DisableModelInvocation))
                    .OrderBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            },
            cancellationToken);
    }
}
