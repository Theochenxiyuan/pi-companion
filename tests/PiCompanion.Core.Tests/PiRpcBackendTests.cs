using System.Collections.Concurrent;
using System.Text.Json;
using PiCompanion.Application.PiRpc;
using PiCompanion.Application.Skills;
using PiCompanion.Core.Agents;
using PiCompanion.Core.Events;
using PiCompanion.Core.Runs;

namespace PiCompanion.Core.Tests;

public sealed class PiRpcBackendTests
{
    [Fact]
    public async Task StartRunAsync_ExposesEffectiveSkillPathsWithoutOverridingNativeLoading()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var globalSkillRoot = Path.Combine(root, ".pi", "agent", "skills", "global-docs");
            var directSkill = Path.Combine(root, ".pi", "agent", "skills", "standalone.md");
            var shadowedGlobal = Path.Combine(root, ".pi", "agent", "skills", "global-shared");
            var workspace = Directory.CreateDirectory(Path.Combine(root, "repo")).FullName;
            var workspaceSkillRoot = Path.Combine(workspace, ".pi", "skills", "local-shared");
            WriteSkill(Path.Combine(globalSkillRoot, "SKILL.md"), "global-docs", "Global skill.");
            WriteSkill(directSkill, "standalone", "Single-file skill.");
            WriteSkill(Path.Combine(shadowedGlobal, "SKILL.md"), "shared", "Shadowed global skill.");
            WriteSkill(Path.Combine(workspaceSkillRoot, "SKILL.md"), "shared", "Workspace winner.");
            var projectTrust = new PiProjectTrustService(root);
            projectTrust.Trust(workspace);

            using var backend = CreateBackend(
                root,
                skillDiscovery: new SkillDiscoveryService(root),
                projectTrust: projectTrust);
            var request = CreateRequest(root, "inspect skill access") with
            {
                WorkingDirectory = workspace,
                ScopeKind = PiCompanion.Core.Tasks.TaskScopeKind.Workspace,
                PermissionMode = "full-access",
            };
            var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            backend.EventReceived += runEvent =>
            {
                if (runEvent.RunId == request.RunId &&
                    runEvent.Kind is CompanionRunEventKind.RunSettled or CompanionRunEventKind.RunFailed)
                {
                    terminal.TrySetResult();
                }
            };

            await backend.StartRunAsync(request, cancellationToken);
            await terminal.Task.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);

            var contextPath = Assert.Single(Directory.GetFiles(
                Path.Combine(root, "sessions", ".runtime"),
                "*.context.json"));
            using var context = JsonDocument.Parse(File.ReadAllText(contextPath));
            var document = context.RootElement;
            Assert.Equal(4, document.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("full-access", document.GetProperty("permissionMode").GetString());
            Assert.Equal("trusted", document.GetProperty("workspaceTrustStatus").GetString());
            var roots = document.GetProperty("skillReadOnlyRoots")
                .EnumerateArray()
                .Select(static item => item.GetString()!)
                .ToArray();
            var files = document.GetProperty("skillReadOnlyFiles")
                .EnumerateArray()
                .Select(static item => item.GetString()!)
                .ToArray();
            Assert.Contains(Path.GetFullPath(globalSkillRoot), roots);
            Assert.Contains(Path.GetFullPath(workspaceSkillRoot), roots);
            Assert.DoesNotContain(Path.GetFullPath(shadowedGlobal), roots);
            Assert.Equal([Path.GetFullPath(directSkill)], files);
            var arguments = JsonSerializer.Deserialize<string[]>(
                File.ReadAllText(Path.Combine(root, "sessions", "fake-args.json"))) ?? [];
            Assert.DoesNotContain("--no-skills", arguments);
            Assert.DoesNotContain("--skill", arguments);
            var promptIndex = Array.IndexOf(arguments, "--append-system-prompt");
            Assert.True(promptIndex >= 0 && promptIndex + 1 < arguments.Length);
            Assert.DoesNotContain("当前工作区未受 Pi 信任", arguments[promptIndex + 1]);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task StartRunAsync_DoesNotGrantReadAccessToUntrustedProjectSkills()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var workspace = Directory.CreateDirectory(Path.Combine(root, "repo")).FullName;
            var globalSkillRoot = Path.Combine(root, ".pi", "agent", "skills", "global-shared");
            var workspaceSkillRoot = Path.Combine(workspace, ".pi", "skills", "local-shared");
            WriteSkill(Path.Combine(globalSkillRoot, "SKILL.md"), "shared", "Global fallback.");
            WriteSkill(Path.Combine(workspaceSkillRoot, "SKILL.md"), "shared", "Untrusted workspace skill.");

            using var backend = CreateBackend(
                root,
                skillDiscovery: new SkillDiscoveryService(root));
            var request = CreateRequest(root, "inspect untrusted skill access") with
            {
                WorkingDirectory = workspace,
                ScopeKind = PiCompanion.Core.Tasks.TaskScopeKind.Workspace,
            };
            var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            backend.EventReceived += runEvent =>
            {
                if (runEvent.RunId == request.RunId &&
                    runEvent.Kind is CompanionRunEventKind.RunSettled or CompanionRunEventKind.RunFailed)
                {
                    terminal.TrySetResult();
                }
            };

            await backend.StartRunAsync(request, cancellationToken);
            await terminal.Task.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);

            var contextPath = Assert.Single(Directory.GetFiles(
                Path.Combine(root, "sessions", ".runtime"),
                "*.context.json"));
            using var context = JsonDocument.Parse(File.ReadAllText(contextPath));
            Assert.Equal(4, context.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(
                "undecided",
                context.RootElement.GetProperty("workspaceTrustStatus").GetString());
            var roots = context.RootElement.GetProperty("skillReadOnlyRoots")
                .EnumerateArray()
                .Select(static item => item.GetString()!)
                .ToArray();
            Assert.Contains(Path.GetFullPath(globalSkillRoot), roots);
            Assert.DoesNotContain(Path.GetFullPath(workspaceSkillRoot), roots);
            var arguments = JsonSerializer.Deserialize<string[]>(
                File.ReadAllText(Path.Combine(root, "sessions", "fake-args.json"))) ?? [];
            var promptIndex = Array.IndexOf(arguments, "--append-system-prompt");
            Assert.True(promptIndex >= 0 && promptIndex + 1 < arguments.Length);
            Assert.Contains("当前工作区未受 Pi 信任", arguments[promptIndex + 1]);
            Assert.Contains("全局技能仍可用", arguments[promptIndex + 1]);
            Assert.Contains("工作区技能管理", arguments[promptIndex + 1]);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task StartRunAsync_ReadPolicyIncludesPiAndAgentSkills()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var piSkill = Path.Combine(root, ".pi", "agent", "skills", "pi-only");
            var agentSkill = Path.Combine(root, ".agents", "skills", "agent-only");
            WriteSkill(Path.Combine(piSkill, "SKILL.md"), "pi-only", "Pi skill.");
            WriteSkill(Path.Combine(agentSkill, "SKILL.md"), "agent-only", "Agent skill.");

            using var backend = CreateBackend(
                root,
                skillDiscovery: new SkillDiscoveryService(root));
            var request = CreateRequest(root, "inspect skill access");
            var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            backend.EventReceived += runEvent =>
            {
                if (runEvent.RunId == request.RunId &&
                    runEvent.Kind is CompanionRunEventKind.RunSettled or CompanionRunEventKind.RunFailed)
                {
                    terminal.TrySetResult();
                }
            };

            await backend.StartRunAsync(request, cancellationToken);
            await terminal.Task.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);

            var arguments = JsonSerializer.Deserialize<string[]>(
                File.ReadAllText(Path.Combine(root, "sessions", "fake-args.json"))) ?? [];
            Assert.DoesNotContain("--no-skills", arguments);
            Assert.DoesNotContain("--skill", arguments);

            var contextPath = Assert.Single(Directory.GetFiles(
                Path.Combine(root, "sessions", ".runtime"),
                "*.context.json"));
            using var context = JsonDocument.Parse(File.ReadAllText(contextPath));
            var roots = context.RootElement.GetProperty("skillReadOnlyRoots")
                .EnumerateArray()
                .Select(static item => item.GetString()!)
                .ToArray();
            Assert.Contains(Path.GetFullPath(piSkill), roots);
            Assert.Contains(Path.GetFullPath(agentSkill), roots);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task GetSessionStatisticsAsync_MapsOfficialRpcStatisticsForWarmSession()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            using var backend = CreateBackend(root);
            var request = CreateRequest(root, "inspect statistics");
            var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            backend.EventReceived += runEvent =>
            {
                if (runEvent.Kind == CompanionRunEventKind.RunSettled) terminal.TrySetResult();
            };

            await backend.StartRunAsync(request, cancellationToken);
            await terminal.Task.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);
            var statistics = await backend.GetSessionStatisticsAsync(
                new AgentSessionStatisticsRequest(
                    request.TaskId,
                    request.WorkingDirectory,
                    request.Model,
                    request.ThinkingLevel,
                    request.PiSessionPath),
                cancellationToken);

            Assert.NotNull(statistics);
            Assert.Equal("fake-session", statistics.SessionId);
            Assert.Equal(11, statistics.TotalMessages);
            Assert.Equal(3, statistics.ToolCalls);
            Assert.Equal(1200, statistics.InputTokens);
            Assert.Equal(800, statistics.CacheReadTokens);
            Assert.Equal(0.0123, statistics.Cost, precision: 4);
            Assert.NotNull(statistics.ContextUsage);
            Assert.Equal(2200, statistics.ContextUsage.Tokens);
            Assert.Equal(128000, statistics.ContextUsage.ContextWindow);
            Assert.Equal(1.71875, statistics.ContextUsage.Percent);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task GetSessionStatisticsAsync_LoadsHistoricalSessionOnlyWhenExplicitlyRequested()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            using var backend = CreateBackend(root);

            async Task RunAndWaitAsync(AgentRunRequest request)
            {
                var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnEvent(CompanionRunEvent runEvent)
                {
                    if (runEvent.RunId == request.RunId && runEvent.Kind == CompanionRunEventKind.RunSettled)
                    {
                        terminal.TrySetResult();
                    }
                }

                backend.EventReceived += OnEvent;
                try
                {
                    await backend.StartRunAsync(request, cancellationToken);
                    await terminal.Task.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);
                }
                finally
                {
                    backend.EventReceived -= OnEvent;
                }
            }

            var historicalRun = CreateRequest(root, "historical statistics source");
            await RunAndWaitAsync(historicalRun);
            var sessionPath = Path.Combine(root, "sessions", "fake-session.jsonl");
            var currentRun = CreateRequest(root, "different current task");
            await RunAndWaitAsync(currentRun);
            var query = new AgentSessionStatisticsRequest(
                historicalRun.TaskId,
                root,
                historicalRun.Model,
                historicalRun.ThinkingLevel,
                sessionPath);

            Assert.Null(await backend.GetSessionStatisticsAsync(query, cancellationToken));
            var statistics = await backend.GetSessionStatisticsAsync(
                query with { LoadHistoricalSession = true },
                cancellationToken);

            Assert.NotNull(statistics);
            Assert.Equal("fake-session", statistics.SessionId);
            Assert.Equal(11, statistics.TotalMessages);
            Assert.Equal("2", File.ReadAllText(Path.Combine(root, "sessions", "fake-start-count.txt")));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task CompactAsync_UsesWarmSessionAndForwardsCustomInstructions()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            using var backend = CreateBackend(root);
            var request = CreateRequest(root, "compact warm session");
            var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            backend.EventReceived += runEvent =>
            {
                if (runEvent.RunId == request.RunId && runEvent.Kind == CompanionRunEventKind.RunSettled)
                {
                    terminal.TrySetResult();
                }
            };

            await backend.StartRunAsync(request, cancellationToken);
            await terminal.Task.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);
            await backend.CompactAsync(
                new AgentSessionCommandRequest(
                    request.TaskId,
                    request.WorkingDirectory,
                    request.Model,
                    request.ThinkingLevel,
                    request.PiSessionPath),
                "保留关键决策",
                cancellationToken);

            var commands = File.ReadLines(Path.Combine(root, "sessions", "fake-command-log.jsonl"))
                .Select(static line => JsonDocument.Parse(line))
                .ToArray();
            try
            {
                var compact = Assert.Single(commands, command =>
                    command.RootElement.GetProperty("type").GetString() == "compact");
                Assert.Equal(
                    "保留关键决策",
                    compact.RootElement.GetProperty("customInstructions").GetString());
            }
            finally
            {
                foreach (var command in commands) command.Dispose();
            }
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task CompactAsync_RestoresHistoricalSessionWhenNoWarmWorkerExists()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            using var backend = CreateBackend(root);
            var sessionPath = Path.Combine(root, "sessions", "historical-session.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(sessionPath)!);
            await File.WriteAllTextAsync(
                sessionPath,
                "{\"type\":\"session\",\"id\":\"historical\"}\n",
                cancellationToken);
            var request = new AgentSessionCommandRequest(
                Guid.NewGuid(),
                root,
                "Pi 默认模型",
                "中",
                sessionPath);

            await backend.CompactAsync(request, cancellationToken: cancellationToken);

            var commands = File.ReadLines(Path.Combine(root, "sessions", "fake-command-log.jsonl"))
                .Select(static line => JsonDocument.Parse(line))
                .ToArray();
            try
            {
                Assert.Contains(commands, command =>
                    command.RootElement.GetProperty("type").GetString() == "switch_session" &&
                    command.RootElement.GetProperty("sessionPath").GetString() == Path.GetFullPath(sessionPath));
                Assert.Contains(commands, command =>
                    command.RootElement.GetProperty("type").GetString() == "compact");
            }
            finally
            {
                foreach (var command in commands) command.Dispose();
            }
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task StartRunAsync_MapsRpcStreamAndSettlesWithStrictSequences()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            using var backend = CreateBackend(root);
            var events = new ConcurrentQueue<CompanionRunEvent>();
            var terminal = new TaskCompletionSource<CompanionRunEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            backend.EventReceived += runEvent =>
            {
                events.Enqueue(runEvent);
                if (runEvent.Kind is CompanionRunEventKind.RunSettled or CompanionRunEventKind.RunFailed)
                {
                    terminal.TrySetResult(runEvent);
                }
            };

            await backend.StartRunAsync(CreateRequest(root, "inspect"), cancellationToken);
            var finalEvent = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);
            var snapshot = events.ToArray();

            Assert.Equal(CompanionRunEventKind.RunSettled, finalEvent.Kind);
            Assert.Equal(RunStatus.Completed, finalEvent.Status);
            Assert.Equal("agent-settled", finalEvent.Payload["settlementSource"]);
            Assert.Equal(Enumerable.Range(1, snapshot.Length).Select(value => (long)value), snapshot.Select(item => item.Sequence));
            Assert.Contains(snapshot, item => item.Kind == CompanionRunEventKind.AssistantTextDelta && item.Payload["delta"] == "真实回答");
            Assert.Contains(snapshot, item =>
                item.Kind == CompanionRunEventKind.ToolStarted &&
                item.Payload["toolCallId"] == "tool-1" &&
                item.Payload["toolName"] == "read" &&
                item.Payload["toolInput"] == "README.md");
            Assert.Contains(snapshot, item =>
                item.Kind == CompanionRunEventKind.ToolCompleted &&
                item.Payload["toolOutput"] == "read result");
            Assert.Contains(snapshot, item => item.Payload.TryGetValue("piSessionId", out var value) && value == "fake-session");
            Assert.Equal(
                ["rpc-connecting", "session-creating", "session-ready", "prompt-submitting"],
                ReadStartupPhases(snapshot));
            Assert.All(
                snapshot.Where(item =>
                    item.Kind != CompanionRunEventKind.RunStarted &&
                    item.Payload.ContainsKey("startupPhase")),
                item => Assert.True(string.IsNullOrWhiteSpace(item.Payload["activity"])));
            Assert.All(
                snapshot.Where(item => item.Payload.ContainsKey("startupPhase")),
                item =>
                {
                    Assert.True(item.Payload.ContainsKey("activityStatus"));
                    Assert.False(item.Payload.ContainsKey("summary"));
                });
            Assert.DoesNotContain(snapshot, item =>
                item.Payload.TryGetValue("summary", out var summary) &&
                summary == "正在初始化 Pi Session");
            Assert.DoesNotContain(snapshot, item =>
                item.Kind == CompanionRunEventKind.WarningRaised &&
                item.Payload.TryGetValue("rawType", out var value) &&
                value == "agent_settled");
            var arguments = JsonSerializer.Deserialize<string[]>(
                File.ReadAllText(Path.Combine(root, "sessions", "fake-args.json"))) ?? [];
            Assert.Contains("--no-extensions", arguments);
            Assert.Contains("--extension", arguments);
            Assert.DoesNotContain("--no-skills", arguments);
            Assert.DoesNotContain("--skill", arguments);
            var toolsIndex = Array.IndexOf(arguments, "--tools");
            Assert.True(toolsIndex >= 0 && toolsIndex + 1 < arguments.Length);
            Assert.Equal(
                "read,grep,find,ls,edit,write,bash,ask_user,list_available_skills",
                arguments[toolsIndex + 1]);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task StartRunAsync_ExposesAgentErrorDetailInTerminalEvent()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            using var backend = CreateBackend(root);
            var terminal = new TaskCompletionSource<CompanionRunEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            backend.EventReceived += runEvent =>
            {
                if (runEvent.Kind == CompanionRunEventKind.RunFailed)
                {
                    terminal.TrySetResult(runEvent);
                }
            };

            await backend.StartRunAsync(CreateRequest(root, "agent-error-detail"), cancellationToken);
            var finalEvent = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);

            const string errorMessage = "Provider rejected the request: invalid model configuration.";
            Assert.Equal(RunStatus.Failed, finalEvent.Status);
            Assert.Equal("agent-error", finalEvent.Payload["exitReason"]);
            Assert.Equal(errorMessage, finalEvent.Payload["errorMessage"]);
            Assert.Contains(errorMessage, finalEvent.Payload["activity"]);
            Assert.Contains(errorMessage, finalEvent.Payload["summary"]);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task StartRunAsync_RoutesTwoConcurrentRunsByRunId()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var firstWorkspace = Directory.CreateDirectory(Path.Combine(root, "first")).FullName;
            var secondWorkspace = Directory.CreateDirectory(Path.Combine(root, "second")).FullName;
            using var backend = CreateBackend(root);
            var first = CreateRequest(firstWorkspace, "wait-for-abort");
            var second = CreateRequest(secondWorkspace, "wait-for-abort");
            var started = new ConcurrentDictionary<Guid, TaskCompletionSource>();
            var terminal = new ConcurrentDictionary<Guid, TaskCompletionSource<CompanionRunEvent>>();
            foreach (var request in new[] { first, second })
            {
                started[request.RunId] = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                terminal[request.RunId] = new TaskCompletionSource<CompanionRunEvent>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            backend.EventReceived += runEvent =>
            {
                if (runEvent.Kind == CompanionRunEventKind.RunStarted)
                {
                    started[runEvent.RunId].TrySetResult();
                }
                if (runEvent.Kind is CompanionRunEventKind.RunSettled or
                    CompanionRunEventKind.RunFailed or
                    CompanionRunEventKind.RunInterrupted)
                {
                    terminal[runEvent.RunId].TrySetResult(runEvent);
                }
            };

            await backend.StartRunAsync(first, cancellationToken);
            await backend.StartRunAsync(second, cancellationToken);
            await Task.WhenAll(
                started[first.RunId].Task,
                started[second.RunId].Task).WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);

            await backend.AbortAsync(first.RunId, cancellationToken);
            await backend.AbortAsync(second.RunId, cancellationToken);
            var outcomes = await Task.WhenAll(
                terminal[first.RunId].Task,
                terminal[second.RunId].Task).WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);

            Assert.All(outcomes, outcome => Assert.Equal(RunStatus.Interrupted, outcome.Status));
            Assert.Equal(
                new[] { first.RunId, second.RunId }.Order(),
                outcomes.Select(outcome => outcome.RunId).Order());
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task StartRunAsync_BoundsToolOutputStoredInTranscriptEvents()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            using var backend = CreateBackend(root);
            var started = new TaskCompletionSource<CompanionRunEvent>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var completed = new TaskCompletionSource<CompanionRunEvent>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            backend.EventReceived += runEvent =>
            {
                if (runEvent.Kind == CompanionRunEventKind.ToolStarted &&
                    runEvent.Payload.TryGetValue("toolName", out var toolName) &&
                    toolName == "web_search")
                {
                    started.TrySetResult(runEvent);
                }

                if (runEvent.Kind == CompanionRunEventKind.ToolCompleted)
                {
                    completed.TrySetResult(runEvent);
                }
            };

            await backend.StartRunAsync(CreateRequest(root, "long-tool-output"), cancellationToken);
            var startedEvent = await started.Task.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);
            var runEvent = await completed.Task.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);
            var output = runEvent.Payload["toolOutput"];

            Assert.Equal("large result", startedEvent.Payload["toolInput"]);
            Assert.Contains("large result", startedEvent.Payload["activity"], StringComparison.Ordinal);
            Assert.Equal(24_001, output.Length);
            Assert.EndsWith("…", output, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task StartRunAsync_LoadsPrivateWebSearchExtensionForSupportedOfficialModel()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var webSearchExtension = Path.Combine(root, "pi-web-search.mjs");
            File.WriteAllText(webSearchExtension, "export default function () {}");
            using var backend = CreateBackend(root, webSearchExtension);
            var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            backend.EventReceived += runEvent =>
            {
                if (runEvent.Kind == CompanionRunEventKind.RunSettled)
                {
                    terminal.TrySetResult();
                }
            };

            var request = CreateRequest(root, "search") with { Model = "openai/gpt-5.4" };
            await backend.StartRunAsync(request, TestContext.Current.CancellationToken);
            await terminal.Task.WaitAsync(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken);

            var arguments = JsonSerializer.Deserialize<string[]>(
                File.ReadAllText(Path.Combine(root, "sessions", "fake-args.json"))) ?? [];
            var toolsIndex = Array.IndexOf(arguments, "--tools");
            Assert.True(toolsIndex >= 0 && toolsIndex + 1 < arguments.Length);
            Assert.Equal(
                "read,grep,find,ls,edit,write,bash,ask_user,list_available_skills,web_search",
                arguments[toolsIndex + 1]);
            Assert.Equal(2, arguments.Count(item => item == "--extension"));
            Assert.Contains(webSearchExtension, arguments);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task StartRunAsync_ReportsRestoringAnExistingSessionDuringColdStartup()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var sessionPath = Path.Combine(root, "sessions", "existing-session.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(sessionPath)!);
            File.WriteAllText(sessionPath, string.Empty);
            using var backend = CreateBackend(root);
            var events = new ConcurrentQueue<CompanionRunEvent>();
            var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            backend.EventReceived += runEvent =>
            {
                events.Enqueue(runEvent);
                if (runEvent.Kind == CompanionRunEventKind.RunSettled)
                {
                    terminal.TrySetResult();
                }
            };
            var request = CreateRequest(root, "restore an existing session") with
            {
                PiSessionPath = sessionPath,
            };

            await backend.StartRunAsync(request, TestContext.Current.CancellationToken);
            await terminal.Task.WaitAsync(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken);

            Assert.Equal(
                ["rpc-connecting", "session-restoring", "session-ready", "prompt-submitting"],
                ReadStartupPhases(events));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task StartRunAsync_PublishesBashCommandInlineWithTheToolEvent()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            using var backend = CreateBackend(root);
            var events = new ConcurrentQueue<CompanionRunEvent>();
            var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            backend.EventReceived += runEvent =>
            {
                events.Enqueue(runEvent);
                if (runEvent.Kind == CompanionRunEventKind.RunSettled) terminal.TrySetResult();
            };

            await backend.StartRunAsync(CreateRequest(root, "test-failure-evidence"), cancellationToken);
            await terminal.Task.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);

            Assert.Contains(events, item =>
                item.Kind == CompanionRunEventKind.ToolStarted &&
                item.Payload["toolName"] == "bash" &&
                item.Payload["toolInput"] == "dotnet test");
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task StartRunAsync_SendsSupportedImagesNativelyAndPassesTheScopedRoot()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var attachmentRoot = Path.Combine(root, "attachments", Guid.NewGuid().ToString("N"));
            var attachment = Path.Combine(attachmentRoot, "run", "image.png");
            Directory.CreateDirectory(Path.GetDirectoryName(attachment)!);
            var imageBytes = new byte[] { 1, 2, 3, 4 };
            File.WriteAllBytes(attachment, imageBytes);
            using var backend = CreateBackend(root);
            var events = new ConcurrentQueue<CompanionRunEvent>();
            var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            backend.EventReceived += runEvent =>
            {
                events.Enqueue(runEvent);
                if (runEvent.Kind is CompanionRunEventKind.RunSettled or CompanionRunEventKind.RunFailed)
                {
                    terminal.TrySetResult();
                }
            };
            var request = CreateRequest(root, "inspect attachment") with
            {
                Attachments = [attachment],
                ReadOnlyAttachmentRoot = attachmentRoot,
            };

            await backend.StartRunAsync(request, TestContext.Current.CancellationToken);
            await terminal.Task.WaitAsync(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken);

            var prompt = File.ReadAllText(Path.Combine(root, "sessions", "fake-last-prompt.txt"));
            Assert.Contains("原生图像输入", prompt, StringComparison.Ordinal);
            Assert.Contains(attachment, prompt, StringComparison.OrdinalIgnoreCase);
            using var images = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "sessions", "fake-last-images.json")));
            var image = Assert.Single(images.RootElement.EnumerateArray());
            Assert.Equal("image/png", image.GetProperty("mimeType").GetString());
            Assert.Equal(imageBytes, Convert.FromBase64String(image.GetProperty("data").GetString()!));
            Assert.Equal(
                Path.GetFullPath(attachmentRoot),
                File.ReadAllText(Path.Combine(root, "sessions", "fake-attachment-root.txt")));
            Assert.Contains("attachments-preparing", ReadStartupPhases(events));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task StartRunAsync_LeavesImagesAsPathAttachmentsForTextOnlyModels()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var attachment = Path.Combine(root, "image.png");
            File.WriteAllBytes(attachment, [1, 2, 3, 4]);
            using var backend = CreateBackend(root);
            var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            backend.EventReceived += runEvent =>
            {
                if (runEvent.Kind == CompanionRunEventKind.RunSettled) terminal.TrySetResult();
            };
            var request = CreateRequest(root, "inspect text-only image") with
            {
                Model = "text-only/model",
                Attachments = [attachment],
            };

            await backend.StartRunAsync(request, TestContext.Current.CancellationToken);
            await terminal.Task.WaitAsync(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken);

            var prompt = File.ReadAllText(Path.Combine(root, "sessions", "fake-last-prompt.txt"));
            Assert.Contains("请使用读取工具查看附件内容后再回答", prompt, StringComparison.Ordinal);
            using var images = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "sessions", "fake-last-images.json")));
            Assert.Empty(images.RootElement.EnumerateArray());
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task StartRunAsync_MapsCompactionAndRetryLifecycleAndCanAbortRetry()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            using var backend = CreateBackend(root);
            var events = new ConcurrentQueue<CompanionRunEvent>();
            var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            backend.EventReceived += runEvent =>
            {
                events.Enqueue(runEvent);
                if (runEvent.Kind == CompanionRunEventKind.RunSettled) terminal.TrySetResult();
            };

            await backend.StartRunAsync(CreateRequest(root, "lifecycle-flow"), cancellationToken);
            await terminal.Task.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);

            Assert.Contains(events, item => item.Kind == CompanionRunEventKind.CompactionStarted && item.Payload["reason"] == "threshold");
            Assert.Contains(events, item => item.Kind == CompanionRunEventKind.CompactionCompleted && item.Payload["success"] == "true");
            Assert.Contains(events, item => item.Kind == CompanionRunEventKind.SummarizationRetryStarted && item.Payload["delayMs"] == "2000");
            Assert.Contains(events, item => item.Kind == CompanionRunEventKind.SummarizationRetryProgressed && item.Payload["source"] == "compaction");
            Assert.Contains(events, item => item.Kind == CompanionRunEventKind.SummarizationRetryCompleted && item.Payload["success"] == "true");
            Assert.Contains(events, item => item.Kind == CompanionRunEventKind.AutoRetryStarted && item.Payload["delayMs"] == "1500");
            Assert.Contains(events, item => item.Kind == CompanionRunEventKind.AutoRetryCompleted && item.Payload["success"] == "true");

            var retryRequest = CreateRequest(root, "retry-wait");
            var retryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var retryEnded = new TaskCompletionSource<CompanionRunEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            backend.EventReceived += runEvent =>
            {
                if (runEvent.RunId != retryRequest.RunId) return;
                if (runEvent.Kind == CompanionRunEventKind.AutoRetryStarted) retryStarted.TrySetResult();
                if (runEvent.Kind == CompanionRunEventKind.AutoRetryCompleted) retryEnded.TrySetResult(runEvent);
            };
            await backend.StartRunAsync(retryRequest, cancellationToken);
            await retryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            await backend.AbortRetryAsync(retryRequest.RunId, cancellationToken);
            var ended = await retryEnded.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            Assert.Equal("false", ended.Payload["success"]);
            Assert.Equal("true", ended.Payload["cancelled"]);
            await backend.AbortAsync(retryRequest.RunId, cancellationToken);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task StartRunAsync_ReconcilesAssistantMessagesSincePersistedEntryCursor()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            using var backend = CreateBackend(root);
            var events = new ConcurrentQueue<CompanionRunEvent>();
            var terminals = new ConcurrentDictionary<Guid, TaskCompletionSource<CompanionRunEvent>>();
            backend.EventReceived += runEvent =>
            {
                events.Enqueue(runEvent);
                if (runEvent.Kind == CompanionRunEventKind.RunSettled && terminals.TryGetValue(runEvent.RunId, out var terminal))
                {
                    terminal.TrySetResult(runEvent);
                }
            };

            var first = CreateRequest(root, "seed-reconcile");
            var firstTerminal = new TaskCompletionSource<CompanionRunEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            terminals[first.RunId] = firstTerminal;
            await backend.StartRunAsync(first, cancellationToken);
            var firstFinal = await firstTerminal.Task.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);
            var cursor = firstFinal.Payload["piEntryCursor"];

            var second = first with
            {
                RunId = Guid.NewGuid(),
                Prompt = "continue after recovery",
                PiSessionPath = Path.Combine(root, "sessions", "fake-session.jsonl"),
                PiEntryCursor = cursor,
                KnownAssistantMessages = ["真实回答"],
            };
            var secondTerminal = new TaskCompletionSource<CompanionRunEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            terminals[second.RunId] = secondTerminal;
            await backend.StartRunAsync(second, cancellationToken);
            await secondTerminal.Task.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);

            var secondEvents = events.Where(item => item.RunId == second.RunId).ToArray();
            Assert.Contains(secondEvents, item =>
                item.Kind == CompanionRunEventKind.AssistantMessageCompleted &&
                item.Payload.TryGetValue("reconciled", out var reconciled) && reconciled == "true" &&
                item.Payload["finalText"] == "从中断窗口恢复的回答");
            Assert.Contains(secondEvents, item =>
                item.Kind == CompanionRunEventKind.SessionSynchronized &&
                item.Payload["recoveredMessageCount"] == "1");
            Assert.Contains("session-reconciling", ReadStartupPhases(secondEvents));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task StartRunAsync_FallsBackToStateCheckWhenSettledEventIsMissing()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            using var backend = CreateBackend(root);
            var terminal = new TaskCompletionSource<CompanionRunEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            backend.EventReceived += runEvent =>
            {
                if (runEvent.Kind is CompanionRunEventKind.RunSettled or CompanionRunEventKind.RunFailed)
                {
                    terminal.TrySetResult(runEvent);
                }
            };

            await backend.StartRunAsync(CreateRequest(root, "legacy-no-settled"), cancellationToken);
            var finalEvent = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);

            Assert.Equal(CompanionRunEventKind.RunSettled, finalEvent.Kind);
            Assert.Equal("agent-end-state-fallback", finalEvent.Payload["settlementSource"]);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task StartRunAsync_ReusesWarmRpcAcrossTasksAndConfigurationChangesWithinWorkspace()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            using var backend = CreateBackend(root);
            var events = new ConcurrentQueue<CompanionRunEvent>();
            var terminals = new ConcurrentDictionary<Guid, TaskCompletionSource<CompanionRunEvent>>();
            backend.EventReceived += runEvent =>
            {
                events.Enqueue(runEvent);
                if (runEvent.Kind == CompanionRunEventKind.RunSettled &&
                    terminals.TryGetValue(runEvent.RunId, out var terminal))
                {
                    terminal.TrySetResult(runEvent);
                }
            };

            var first = CreateRequest(root, "first turn");
            await RunAndWaitAsync(first);

            var second = first with
            {
                RunId = Guid.NewGuid(),
                Prompt = "second turn",
                PiSessionPath = Path.Combine(root, "sessions", "fake-session.jsonl"),
            };
            await RunAndWaitAsync(second);
            await Task.Delay(650, cancellationToken);

            Assert.Equal("1", File.ReadAllText(Path.Combine(root, "sessions", "fake-start-count.txt")));
            var secondEvents = events.Where(item => item.RunId == second.RunId).ToArray();
            Assert.Equal(Enumerable.Range(1, secondEvents.Length).Select(value => (long)value), secondEvents.Select(item => item.Sequence));
            Assert.Contains(secondEvents, item =>
                item.Kind == CompanionRunEventKind.RunStarted &&
                item.Status == RunStatus.Starting &&
                !item.Payload.ContainsKey("runtimePath"));
            Assert.Equal(
                ["rpc-reused", "session-continuing", "session-configuring", "session-ready", "prompt-submitting"],
                ReadStartupPhases(secondEvents));
            Assert.DoesNotContain(secondEvents, item => item.Kind == CompanionRunEventKind.RunFailed);

            var changed = second with
            {
                RunId = Guid.NewGuid(),
                Prompt = "configuration changed",
                ThinkingLevel = "high",
            };
            await RunAndWaitAsync(changed);
            Assert.Equal("1", File.ReadAllText(Path.Combine(root, "sessions", "fake-start-count.txt")));

            var otherTask = changed with
            {
                TaskId = Guid.NewGuid(),
                RunId = Guid.NewGuid(),
                Prompt = "another task in the same workspace",
                PiSessionPath = null,
            };
            await RunAndWaitAsync(otherTask);
            Assert.Equal("1", File.ReadAllText(Path.Combine(root, "sessions", "fake-start-count.txt")));
            Assert.Equal(
                ["rpc-reused", "session-creating", "session-configuring", "session-ready", "prompt-submitting"],
                ReadStartupPhases(events.Where(item => item.RunId == otherTask.RunId)));
            var commands = File.ReadLines(Path.Combine(root, "sessions", "fake-command-log.jsonl"))
                .Select(line => JsonDocument.Parse(line))
                .ToArray();
            try
            {
                Assert.Contains(commands, command =>
                    command.RootElement.GetProperty("type").GetString() == "new_session");
                Assert.Contains(commands, command =>
                    command.RootElement.GetProperty("type").GetString() == "set_thinking_level" &&
                    command.RootElement.GetProperty("level").GetString() == "high");
                Assert.Contains(commands, command =>
                    command.RootElement.GetProperty("type").GetString() == "set_steering_mode" &&
                    command.RootElement.GetProperty("mode").GetString() == "one-at-a-time");
                Assert.Contains(commands, command =>
                    command.RootElement.GetProperty("type").GetString() == "set_follow_up_mode" &&
                    command.RootElement.GetProperty("mode").GetString() == "one-at-a-time");
            }
            finally
            {
                foreach (var command in commands) command.Dispose();
            }

            async Task RunAndWaitAsync(AgentRunRequest request)
            {
                var terminal = new TaskCompletionSource<CompanionRunEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
                terminals[request.RunId] = terminal;
                await backend.StartRunAsync(request, cancellationToken);
                await terminal.Task.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);
            }
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task InvalidateIdleResources_RebuildsOnlyTheAffectedWarmWorkspace()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var firstWorkspace = Directory.CreateDirectory(Path.Combine(root, "first-workspace")).FullName;
            var secondWorkspace = Directory.CreateDirectory(Path.Combine(root, "second-workspace")).FullName;
            using var backend = CreateBackend(root);
            var terminals = new ConcurrentDictionary<Guid, TaskCompletionSource>();
            backend.EventReceived += runEvent =>
            {
                if (runEvent.Kind == CompanionRunEventKind.RunSettled &&
                    terminals.TryGetValue(runEvent.RunId, out var terminal))
                {
                    terminal.TrySetResult();
                }
            };

            await RunAsync(CreateRequest(firstWorkspace, "first"));
            await RunAsync(CreateRequest(secondWorkspace, "second"));
            Assert.Equal("2", File.ReadAllText(Path.Combine(root, "sessions", "fake-start-count.txt")));

            ((IAgentBackendResourceInvalidator)backend).InvalidateIdleResources(firstWorkspace);
            await RunAsync(CreateRequest(secondWorkspace, "reuse unaffected"));
            Assert.Equal("2", File.ReadAllText(Path.Combine(root, "sessions", "fake-start-count.txt")));
            await RunAsync(CreateRequest(firstWorkspace, "rebuild affected"));
            Assert.Equal("3", File.ReadAllText(Path.Combine(root, "sessions", "fake-start-count.txt")));

            async Task RunAsync(AgentRunRequest request)
            {
                var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                terminals[request.RunId] = terminal;
                await backend.StartRunAsync(request, cancellationToken);
                await terminal.Task.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);
            }
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task PrepareAsync_HidesFirstRunProcessStartupAndKeepsOnlyTwoWarmWorkspaces()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            var workspaces = Enumerable.Range(1, 3)
                .Select(index => Path.Combine(root, $"workspace-{index}"))
                .ToArray();
            foreach (var workspace in workspaces) Directory.CreateDirectory(workspace);

            using var backend = CreateBackend(root);
            foreach (var workspace in workspaces)
            {
                await backend.PrepareAsync(
                    new AgentPreparationRequest(workspace, "provider/model", "medium"),
                    cancellationToken);
            }

            Assert.Equal("3", File.ReadAllText(Path.Combine(root, "sessions", "fake-start-count.txt")));

            var request = CreateRequest(workspaces[0], "use evicted workspace") with
            {
                Model = "provider/model",
            };
            var events = new ConcurrentQueue<CompanionRunEvent>();
            var awaitedRunId = request.RunId;
            var terminal = new TaskCompletionSource<CompanionRunEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            backend.EventReceived += runEvent =>
            {
                events.Enqueue(runEvent);
                if (runEvent.RunId == awaitedRunId && runEvent.Kind == CompanionRunEventKind.RunSettled)
                {
                    terminal.TrySetResult(runEvent);
                }
            };

            await backend.StartRunAsync(request, cancellationToken);
            await terminal.Task.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);
            Assert.Equal("4", File.ReadAllText(Path.Combine(root, "sessions", "fake-start-count.txt")));

            var second = request with
            {
                TaskId = Guid.NewGuid(),
                RunId = Guid.NewGuid(),
                WorkingDirectory = workspaces[2],
                Prompt = "use retained workspace",
            };
            terminal = new TaskCompletionSource<CompanionRunEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            awaitedRunId = second.RunId;
            await backend.StartRunAsync(second, cancellationToken);
            await terminal.Task.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);
            Assert.Equal("4", File.ReadAllText(Path.Combine(root, "sessions", "fake-start-count.txt")));
            Assert.Equal(
                ["rpc-reused", "session-prewarmed", "session-configuring", "session-ready", "prompt-submitting"],
                ReadStartupPhases(events.Where(item => item.RunId == second.RunId)));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task AbortAsync_EndsRunAsInterrupted()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            using var backend = CreateBackend(root);
            var events = new ConcurrentQueue<CompanionRunEvent>();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var terminal = new TaskCompletionSource<CompanionRunEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            backend.EventReceived += runEvent =>
            {
                events.Enqueue(runEvent);
                if (runEvent.Kind == CompanionRunEventKind.RunStarted && runEvent.Status == RunStatus.Running)
                {
                    started.TrySetResult();
                }

                if (runEvent.Kind == CompanionRunEventKind.RunInterrupted)
                {
                    terminal.TrySetResult(runEvent);
                }
            };
            var request = CreateRequest(root, "wait-for-abort");

            await backend.StartRunAsync(request, cancellationToken);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            await backend.SteerAsync(request.RunId, "只检查配置", cancellationToken);
            await backend.AbortAsync(request.RunId, cancellationToken);
            var finalEvent = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);

            Assert.Equal(RunStatus.Interrupted, finalEvent.Status);
            Assert.Equal("user-abort", finalEvent.Payload["exitReason"]);
            Assert.Contains(events, item =>
                item.Kind == CompanionRunEventKind.UserMessageAdded &&
                item.Payload["message"] == "只检查配置" &&
                item.Payload["delivery"] == "steer");
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task AbortAsync_ForcesStopWhenPendingInteractionBlocksRpcResponse()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            using var backend = CreateBackend(root);
            var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var terminal = new TaskCompletionSource<CompanionRunEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            backend.EventReceived += runEvent =>
            {
                if (runEvent.Kind == CompanionRunEventKind.ApprovalRequested)
                {
                    requested.TrySetResult();
                }

                if (runEvent.Kind == CompanionRunEventKind.RunInterrupted)
                {
                    terminal.TrySetResult(runEvent);
                }
            };
            var request = CreateRequest(root, "permission-flow ignore-abort-response");

            await backend.StartRunAsync(request, cancellationToken);
            await requested.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            await backend.AbortAsync(request.RunId, cancellationToken).WaitAsync(TimeSpan.FromSeconds(1), cancellationToken);
            var finalEvent = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(7), cancellationToken);

            Assert.Equal(RunStatus.Interrupted, finalEvent.Status);
            Assert.Equal("abort-timeout", finalEvent.Payload["exitReason"]);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task PermissionRequest_DoesNotExecuteToolUntilApproved()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            using var backend = CreateBackend(root);
            var events = new ConcurrentQueue<CompanionRunEvent>();
            var requested = new TaskCompletionSource<CompanionRunEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            var terminal = new TaskCompletionSource<CompanionRunEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            backend.EventReceived += runEvent =>
            {
                events.Enqueue(runEvent);
                if (runEvent.Kind == CompanionRunEventKind.ApprovalRequested) requested.TrySetResult(runEvent);
                if (runEvent.Kind == CompanionRunEventKind.RunSettled) terminal.TrySetResult(runEvent);
            };
            var request = CreateRequest(root, "permission-flow");

            await backend.StartRunAsync(request, cancellationToken);
            var permission = await requested.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            await Task.Delay(150, cancellationToken);

            Assert.Equal(RunStatus.WaitingForApproval, permission.Status);
            Assert.DoesNotContain(events, item => item.Kind == CompanionRunEventKind.ToolStarted);
            var options = JsonSerializer.Deserialize<string[]>(permission.Payload["interactionOptions"]) ?? [];
            Assert.Equal(["允许一次", "本任务内允许同类操作", "拒绝"], options);

            await backend.ResolveInteractionAsync(
                request.RunId,
                new InteractionResolution(true, "允许一次", permission.Payload["interactionId"]),
                cancellationToken);
            await terminal.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

            Assert.Contains(events, item => item.Kind == CompanionRunEventKind.InteractionResolved);
            Assert.Contains(events, item => item.Kind == CompanionRunEventKind.ToolStarted && item.Payload["toolName"] == "bash");
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task PermissionRequest_AfterWarmReuseUsesTheNewRunPermissionToken()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            using var backend = CreateBackend(root);
            var first = CreateRequest(root, "warm the runtime");
            var firstTerminal = new TaskCompletionSource<CompanionRunEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            backend.EventReceived += runEvent =>
            {
                if (runEvent.RunId == first.RunId && runEvent.Kind == CompanionRunEventKind.RunSettled)
                {
                    firstTerminal.TrySetResult(runEvent);
                }
            };
            await backend.StartRunAsync(first, cancellationToken);
            await firstTerminal.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

            var second = first with
            {
                RunId = Guid.NewGuid(),
                Prompt = "permission-flow",
                PiSessionPath = Path.Combine(root, "sessions", "fake-session.jsonl"),
            };
            var requested = new TaskCompletionSource<CompanionRunEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            var terminal = new TaskCompletionSource<CompanionRunEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            backend.EventReceived += runEvent =>
            {
                if (runEvent.RunId != second.RunId) return;
                if (runEvent.Kind == CompanionRunEventKind.ApprovalRequested) requested.TrySetResult(runEvent);
                if (runEvent.Kind == CompanionRunEventKind.RunSettled) terminal.TrySetResult(runEvent);
            };

            await backend.StartRunAsync(second, cancellationToken);
            var permission = await requested.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            await backend.ResolveInteractionAsync(
                second.RunId,
                new InteractionResolution(true, "允许一次", permission.Payload["interactionId"]),
                cancellationToken);
            await terminal.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

            Assert.Equal("1", File.ReadAllText(Path.Combine(root, "sessions", "fake-start-count.txt")));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task PermissionRequest_DenialNeverExecutesTool()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            using var backend = CreateBackend(root);
            var events = new ConcurrentQueue<CompanionRunEvent>();
            var requested = new TaskCompletionSource<CompanionRunEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            var terminal = new TaskCompletionSource<CompanionRunEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            backend.EventReceived += runEvent =>
            {
                events.Enqueue(runEvent);
                if (runEvent.Kind == CompanionRunEventKind.ApprovalRequested) requested.TrySetResult(runEvent);
                if (runEvent.Kind == CompanionRunEventKind.RunSettled) terminal.TrySetResult(runEvent);
            };
            var request = CreateRequest(root, "permission-flow");

            await backend.StartRunAsync(request, cancellationToken);
            var permission = await requested.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            await backend.ResolveInteractionAsync(
                request.RunId,
                new InteractionResolution(false, InteractionId: permission.Payload["interactionId"]),
                cancellationToken);
            await terminal.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

            Assert.DoesNotContain(events, item => item.Kind == CompanionRunEventKind.ToolStarted);
            Assert.Contains(events, item =>
                item.Kind == CompanionRunEventKind.InteractionResolved && item.Payload["approved"] == "false");
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task QuestionRequest_ExposesChoicesAndReturnsSelectedAnswer()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            using var backend = CreateBackend(root);
            var requested = new TaskCompletionSource<CompanionRunEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            var resolved = new TaskCompletionSource<CompanionRunEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            var terminal = new TaskCompletionSource<CompanionRunEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            backend.EventReceived += runEvent =>
            {
                if (runEvent.Kind == CompanionRunEventKind.QuestionRequested) requested.TrySetResult(runEvent);
                if (runEvent.Kind == CompanionRunEventKind.InteractionResolved) resolved.TrySetResult(runEvent);
                if (runEvent.Kind == CompanionRunEventKind.RunSettled) terminal.TrySetResult(runEvent);
            };
            var request = CreateRequest(root, "question-flow");

            await backend.StartRunAsync(request, cancellationToken);
            var question = await requested.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            Assert.Equal(RunStatus.WaitingForAnswer, question.Status);
            Assert.Equal(
                ["权限策略", "队列状态"],
                JsonSerializer.Deserialize<string[]>(question.Payload["interactionOptions"]) ?? []);

            await backend.ResolveInteractionAsync(
                request.RunId,
                new InteractionResolution(true, "队列状态", question.Payload["interactionId"]),
                cancellationToken);
            var resolution = await resolved.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            await terminal.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

            Assert.Equal("队列状态", resolution.Payload["response"]);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task QuestionRequest_CancelDoesNotReturnTheDefaultChoice()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            using var backend = CreateBackend(root);
            var requested = new TaskCompletionSource<CompanionRunEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            var resolved = new TaskCompletionSource<CompanionRunEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            var terminal = new TaskCompletionSource<CompanionRunEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            backend.EventReceived += runEvent =>
            {
                if (runEvent.Kind == CompanionRunEventKind.QuestionRequested) requested.TrySetResult(runEvent);
                if (runEvent.Kind == CompanionRunEventKind.InteractionResolved) resolved.TrySetResult(runEvent);
                if (runEvent.Kind == CompanionRunEventKind.RunSettled) terminal.TrySetResult(runEvent);
            };
            var request = CreateRequest(root, "question-flow");

            await backend.StartRunAsync(request, cancellationToken);
            var question = await requested.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            await backend.ResolveInteractionAsync(
                request.RunId,
                new InteractionResolution(false, InteractionId: question.Payload["interactionId"]),
                cancellationToken);
            var resolution = await resolved.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            await terminal.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

            Assert.Equal("false", resolution.Payload["approved"]);
            Assert.False(resolution.Payload.ContainsKey("response"));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task QuestionRequest_WithOtherChoiceAcceptsCustomAnswer()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            using var backend = CreateBackend(root);
            var requested = new TaskCompletionSource<CompanionRunEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            var resolved = new TaskCompletionSource<CompanionRunEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            var terminal = new TaskCompletionSource<CompanionRunEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            backend.EventReceived += runEvent =>
            {
                if (runEvent.Kind == CompanionRunEventKind.QuestionRequested) requested.TrySetResult(runEvent);
                if (runEvent.Kind == CompanionRunEventKind.InteractionResolved) resolved.TrySetResult(runEvent);
                if (runEvent.Kind == CompanionRunEventKind.RunSettled) terminal.TrySetResult(runEvent);
            };
            var request = CreateRequest(root, "custom-question-flow");

            await backend.StartRunAsync(request, cancellationToken);
            var question = await requested.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            Assert.Equal(
                ["权限策略", "队列状态", "其他…"],
                JsonSerializer.Deserialize<string[]>(question.Payload["interactionOptions"]) ?? []);

            await backend.ResolveInteractionAsync(
                request.RunId,
                new InteractionResolution(true, "检查日志聚合", question.Payload["interactionId"]),
                cancellationToken);
            var resolution = await resolved.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            await terminal.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

            Assert.Equal("检查日志聚合", resolution.Payload["response"]);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task SteerAndFollowUp_PublishQueueContents()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            using var backend = CreateBackend(root);
            var queueEvents = new ConcurrentQueue<CompanionRunEvent>();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            backend.EventReceived += runEvent =>
            {
                if (runEvent.Status == RunStatus.Running) started.TrySetResult();
                if (runEvent.Kind == CompanionRunEventKind.QueueChanged && runEvent.Payload.ContainsKey("steeringQueue"))
                {
                    queueEvents.Enqueue(runEvent);
                }
            };
            var request = CreateRequest(root, "wait-for-abort");

            await backend.StartRunAsync(request, cancellationToken);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            await backend.SteerAsync(request.RunId, "先检查权限", cancellationToken);
            await backend.FollowUpAsync(request.RunId, "然后汇总", cancellationToken);
            await WaitUntilAsync(() => queueEvents.Count >= 2, cancellationToken);
            var latest = queueEvents.Last();

            Assert.Equal(["先检查权限"], JsonSerializer.Deserialize<string[]>(latest.Payload["steeringQueue"]) ?? []);
            Assert.Equal(["然后汇总"], JsonSerializer.Deserialize<string[]>(latest.Payload["followUpQueue"]) ?? []);
            await backend.AbortAsync(request.RunId, cancellationToken);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task ToolExecutionCompleted_PreservesPinnedPiEditPatchAndArguments()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            using var backend = CreateBackend(root);
            var tool = new TaskCompletionSource<AgentToolExecution>(TaskCreationOptions.RunContinuationsAsynchronously);
            backend.ToolExecutionCompleted += execution => tool.TrySetResult(execution);

            await backend.StartRunAsync(CreateRequest(root, "edit-evidence"), cancellationToken);
            var execution = await tool.Task.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);
            using var arguments = JsonDocument.Parse(execution.ArgumentsJson);
            using var result = JsonDocument.Parse(execution.ResultJson);

            Assert.Equal("edit", execution.ToolName);
            Assert.Equal("sample.txt", arguments.RootElement.GetProperty("path").GetString());
            Assert.Contains("--- a/sample.txt", result.RootElement.GetProperty("details").GetProperty("patch").GetString());
            Assert.False(execution.IsError);
            Assert.True(execution.CompletedAt >= execution.StartedAt);
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    private static PiRpcBackend CreateBackend(
        string root,
        string? webSearchExtensionPath = null,
        SkillDiscoveryService? skillDiscovery = null,
        PiProjectTrustService? projectTrust = null)
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "fake-pi-rpc.js");
        var extension = Path.Combine(AppContext.BaseDirectory, "Fixtures", "pi-companion.mjs");
        return new PiRpcBackend(
            new PiRuntimeResolver(fixture, root, "node.exe"),
            Path.Combine(root, "sessions"),
            Path.Combine(root, "logs"),
            extension,
            Path.Combine(root, "backups"),
            webSearchExtensionPath: webSearchExtensionPath,
            skillDiscovery: skillDiscovery,
            projectTrust: projectTrust ?? new PiProjectTrustService(root));
    }

    private static AgentRunRequest CreateRequest(string root, string prompt) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        "测试任务",
        prompt,
        root,
        "Pi 默认模型",
        "中",
        "Success");

    private static void WriteSkill(string path, string name, string description)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(
            path,
            $"---{Environment.NewLine}name: {name}{Environment.NewLine}description: {description}{Environment.NewLine}---{Environment.NewLine}");
    }

    private static string[] ReadStartupPhases(IEnumerable<CompanionRunEvent> events) =>
        events
            .Where(item => item.Payload.ContainsKey("startupPhase"))
            .Select(item => item.Payload["startupPhase"])
            .ToArray();

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "PiCompanionTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("Timed out waiting for the expected RPC event.");
            await Task.Delay(20, cancellationToken);
        }
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 19)
            {
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException) when (attempt < 19)
            {
                Thread.Sleep(50);
            }
        }
    }
}
