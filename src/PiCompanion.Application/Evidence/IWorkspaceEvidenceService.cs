using PiCompanion.Core.Agents;
using PiCompanion.Core.Evidence;

namespace PiCompanion.Application.Evidence;

public interface IWorkspaceEvidenceService : IDisposable
{
    event Action<Guid>? EvidenceChanged;

    void BeginRun(Guid taskId, Guid runId, string workingDirectory);

    void RecordToolExecution(AgentToolExecution execution);

    void FinalizeRun(Guid runId);

    RunEvidenceSnapshot GetRunEvidence(Guid runId);

    FileDiffEvidence? GetFileDiff(Guid fileChangeId);

    RecoveryResult RestoreFile(Guid fileChangeId);
}
