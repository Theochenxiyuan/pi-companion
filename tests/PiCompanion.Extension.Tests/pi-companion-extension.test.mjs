import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import test from "node:test";

import piCompanionExtension, {
	classifyToolCall,
	isPathInsideWorkspace,
	permissionChoices,
	resolveToolTarget,
} from "../../src/PiCompanion.Extension/pi-companion.mjs";
import {
	applyDeveloperRoleCapabilities,
	computeModelsConfigRevision,
	defaultSupportsDeveloperRole,
	insertProviderIntoModelsJson,
	mergeDeveloperRoleCapabilities,
	normalizeCustomProvider,
	removeProviderFromModelsJson,
	replaceProviderInModelsJson,
	toCustomProviderInfo,
	toModelsJsonProvider,
} from "../../src/PiCompanion.Extension/pi-models-config.mjs";

function temporaryDirectory() {
	return fs.mkdtempSync(path.join(os.tmpdir(), "pi-companion-extension-"));
}

function loadExtension(
	root,
	backupDirectory = path.join(root, "backups"),
	grantDirectory = path.join(root, "permission-grants"),
	permissionMode = "standard",
) {
	process.env.PI_COMPANION_WORKING_DIRECTORY = root;
	process.env.PI_COMPANION_BACKUP_DIRECTORY = backupDirectory;
	process.env.PI_COMPANION_RUN_ID = "test-run";
	delete process.env.PI_COMPANION_RUN_ID_FILE;
	delete process.env.PI_COMPANION_CONTEXT_FILE;
	process.env.PI_COMPANION_TASK_ID = "test-task";
	process.env.PI_COMPANION_GRANT_DIRECTORY = grantDirectory;
	process.env.PI_COMPANION_PERMISSION_MODE = permissionMode;
	const handlers = new Map();
	const tools = new Map();
	piCompanionExtension({
		on: (name, handler) => handlers.set(name, handler),
		registerTool: (tool) => tools.set(tool.name, tool),
	});
	return { handlers, tools, backupDirectory, grantDirectory };
}

function uiContext(select) {
	const notifications = [];
	return {
		notifications,
		context: {
			hasUI: true,
			ui: {
				select,
				input: async () => undefined,
				notify: (message, type) => notifications.push({ message, type }),
			},
		},
	};
}

test("ask_user prefers Pi 0.83 strict JSON Schema sampling", () => {
	const root = temporaryDirectory();
	try {
		const { tools } = loadExtension(root);
		const askUser = tools.get("ask_user");
		assert.deepEqual(askUser.constrainedSampling, { type: "json_schema", strict: "prefer" });
		assert.equal(askUser.parameters.additionalProperties, false);
		assert.equal(askUser.parameters.properties.choices.type, "array");
		assert.equal(askUser.parameters.properties.allowOther.type, "boolean");
		assert.deepEqual(
			new Set(askUser.parameters.required),
			new Set(Object.keys(askUser.parameters.properties)),
		);
	} finally {
		fs.rmSync(root, { recursive: true, force: true });
	}
});

test("all Companion tools satisfy OpenAI strict required-property rules", () => {
	const root = temporaryDirectory();
	const artifactDirectory = path.join(root, "artifacts");
	try {
		process.env.PI_COMPANION_ARTIFACT_DIRECTORY = artifactDirectory;
		const { tools } = loadExtension(root);
		for (const tool of tools.values()) {
			assert.equal(tool.parameters.type, "object", tool.name);
			assert.equal(tool.parameters.additionalProperties, false, tool.name);
			assert.deepEqual(
				new Set(tool.parameters.required),
				new Set(Object.keys(tool.parameters.properties)),
				tool.name,
			);
		}
	} finally {
		delete process.env.PI_COMPANION_ARTIFACT_DIRECTORY;
		fs.rmSync(root, { recursive: true, force: true });
	}
});

test("custom provider config preserves JSONC comments and trailing commas", () => {
	const source = `{
	// Existing user configuration must survive.
	"providers": {
		"existing": {
			"baseUrl": "http://localhost:11434/v1",
			"api": "openai-completions",
			"apiKey": "local",
			"models": [{ "id": "existing-model" }],
		},
	},
}\n`;
	const provider = normalizeCustomProvider({
		id: "company-gateway",
		name: "Company Gateway",
		baseUrl: "https://models.example.com/v1/",
		api: "openai-completions",
		credentialMode: "api-key",
		models: [{
			id: "company-coder",
			name: "Company Coder",
			reasoning: true,
			imageInput: true,
			contextWindow: 128000,
			maxTokens: 16384,
		}],
	});

	const updated = insertProviderIntoModelsJson(source, provider.id, toModelsJsonProvider(provider));
	assert.match(updated, /Existing user configuration must survive/u);
	assert.match(updated, /"existing"/u);
	assert.match(updated, /"company-gateway"/u);
	assert.match(updated, /"input": \[\s*"text",\s*"image"/u);
	assert.equal(provider.baseUrl, "https://models.example.com/v1");
	assert.notEqual(computeModelsConfigRevision(source), computeModelsConfigRevision(updated));
});

test("custom provider config creates a providers object when models.json only has other settings", () => {
	const source = `{
	// Keep unrelated Pi model settings.
	"modelOverrides": {
		"openai/gpt-5": { "name": "Work GPT" },
	},
}\n`;
	const provider = normalizeCustomProvider({
		id: "local-gateway",
		name: "Local Gateway",
		baseUrl: "http://127.0.0.1:11434/v1",
		api: "openai-completions",
		credentialMode: "local",
		models: [{ id: "local-model", contextWindow: 32000, maxTokens: 4096 }],
	});

	const updated = insertProviderIntoModelsJson(source, provider.id, toModelsJsonProvider(provider));
	assert.match(updated, /Keep unrelated Pi model settings/u);
	assert.match(updated, /"modelOverrides"/u);
	assert.match(updated, /"providers"\s*:\s*\{/u);
	assert.match(updated, /"local-gateway"/u);
});

test("custom provider config replaces one provider without rewriting unrelated JSONC", () => {
	const source = `{
	// Keep this user comment and sibling provider.
	"providers": {
		"company-gateway": {
			"name": "Old Company Gateway",
			"baseUrl": "https://old.example.com/v1",
			"api": "openai-completions",
			"models": [{ "id": "old-model" }],
		},
		"sibling": { "baseUrl": "http://localhost:11434/v1", "models": [], },
	},
}
`;
	const provider = normalizeCustomProvider({
		id: "company-gateway",
		name: "New Company Gateway",
		baseUrl: "https://new.example.com/v1",
		api: "openai-responses",
		credentialMode: "local",
		models: [{ id: "new-model", contextWindow: 64000, maxTokens: 8192 }],
	});

	const updated = replaceProviderInModelsJson(source, provider.id, toModelsJsonProvider(provider));
	assert.match(updated, /Keep this user comment and sibling provider/u);
	assert.match(updated, /"sibling"/u);
	assert.match(updated, /"new-model"/u);
	assert.match(updated, /"apiKey": "local"/u);
	assert.doesNotMatch(updated, /old\.example\.com/u);
	assert.doesNotMatch(updated, /old-model/u);
});

test("custom provider config removes only the selected provider from JSONC", () => {
	const source = `{
	// Keep this user comment and sibling provider.
	"providers": {
		"company-gateway": { "baseUrl": "https://models.example.com/v1", "models": [], },
		"sibling": { "baseUrl": "http://localhost:11434/v1", "models": [], },
	},
	"modelOverrides": { "sibling/model": { "name": "Local" }, },
}
`;

	const updated = removeProviderFromModelsJson(source, "company-gateway");
	assert.doesNotMatch(updated, /"company-gateway"/u);
	assert.match(updated, /Keep this user comment and sibling provider/u);
	assert.match(updated, /"sibling"/u);
	assert.match(updated, /"modelOverrides"/u);
});

test("custom provider config can remove the only provider", () => {
	const source = `{
	"providers": {
		"company-gateway": { "models": [], }
	},
	"other": true
}
`;

	const updated = removeProviderFromModelsJson(source, "company-gateway");
	assert.match(updated, /"providers"\s*:\s*\{\s*\}/u);
	assert.match(updated, /"other": true/u);
});

test("custom provider config rejects duplicate models and invalid URLs", () => {
	const base = {
		id: "custom",
		name: "Custom",
		baseUrl: "not-a-url",
		api: "openai-completions",
		credentialMode: "local",
		models: [{ id: "model", contextWindow: 128000, maxTokens: 4096 }],
	};
	assert.throws(() => normalizeCustomProvider(base), /Base URL/u);
	assert.throws(() => normalizeCustomProvider({
		...base,
		baseUrl: "http://localhost:1234/v1",
		models: [
			{ id: "model", contextWindow: 128000, maxTokens: 4096 },
			{ id: "model", contextWindow: 128000, maxTokens: 4096 },
		],
	}), /重复/u);
});

test("custom OpenAI-compatible providers default conservatively without exposing a role setting", () => {
	const provider = normalizeCustomProvider({
		id: "company-gateway",
		name: "Company Gateway",
		baseUrl: "https://models.example.com/v1",
		api: "openai-completions",
		credentialMode: "api-key",
		models: [{ id: "reasoning-model", reasoning: true, contextWindow: 128000, maxTokens: 8192 }],
	});

	const resolved = applyDeveloperRoleCapabilities(provider, null);
	const config = toModelsJsonProvider(resolved);
	assert.equal(resolved.models[0].supportsDeveloperRole, false);
	assert.deepEqual(config.models[0].compat, { supportsDeveloperRole: false });
	assert.equal(toCustomProviderInfo(provider.id, config).models[0].supportsDeveloperRole, false);
	assert.equal(defaultSupportsDeveloperRole("https://api.openai.com/v1"), true);
	assert.equal(defaultSupportsDeveloperRole("https://example.openai.azure.com/openai/v1"), true);
	assert.equal(defaultSupportsDeveloperRole("https://models.example.com/v1"), false);
});

test("model catalog developer_role metadata overrides endpoint defaults per model", () => {
	const provider = normalizeCustomProvider({
		id: "neuralwatt",
		name: "Neuralwatt",
		baseUrl: "https://api.neuralwatt.com/v1",
		api: "openai-completions",
		credentialMode: "api-key",
		models: [
			{ id: "deepseek-v4-flash", reasoning: true, contextWindow: 1048560, maxTokens: 65536 },
			{ id: "unlisted-model", reasoning: true, contextWindow: 128000, maxTokens: 8192 },
		],
	});

	const resolved = applyDeveloperRoleCapabilities(provider, {
		data: [{
			id: "deepseek-v4-flash",
			metadata: { capabilities: { developer_role: true } },
		}],
	});
	assert.equal(resolved.models[0].supportsDeveloperRole, true);
	assert.equal(resolved.models[1].supportsDeveloperRole, false);

	const existing = {
		name: "Neuralwatt",
		baseUrl: provider.baseUrl,
		api: provider.api,
		headers: { "X-Existing": "preserved" },
		models: [{
			id: "deepseek-v4-flash",
			compat: { supportsStore: false },
		}],
	};
	const merged = mergeDeveloperRoleCapabilities(existing, resolved);
	assert.deepEqual(merged.headers, { "X-Existing": "preserved" });
	assert.deepEqual(merged.models[0].compat, {
		supportsStore: false,
		supportsDeveloperRole: true,
	});
	assert.equal(mergeDeveloperRoleCapabilities(merged, resolved), merged);
});

test("workspace path policy canonicalizes traversal and allows descendants", () => {
	const root = temporaryDirectory();
	try {
		const child = path.join(root, "src", "file.txt");
		fs.mkdirSync(path.dirname(child), { recursive: true });
		fs.writeFileSync(child, "content");
		assert.equal(isPathInsideWorkspace(child, root), true);
		assert.equal(resolveToolTarget("src/../src/file.txt", root), fs.realpathSync.native(child));
		assert.equal(classifyToolCall({ toolName: "read", input: { path: child } }, root).action, "allow");
		assert.equal(classifyToolCall({ toolName: "ask_user", input: { question: "继续吗？" } }, root).action, "allow");
		assert.deepEqual(
			classifyToolCall({ toolName: "web_search", input: { query: "今天的新闻" } }, root),
			{ action: "allow", permissionClass: "network:web-search", target: undefined },
		);
		assert.equal(classifyToolCall({ toolName: "read", input: { path: path.dirname(root) } }, root).action, "block");
	} finally {
		fs.rmSync(root, { recursive: true, force: true });
	}
});

test("workspace path policy blocks a junction that escapes the workspace", (t) => {
	const root = temporaryDirectory();
	const outside = temporaryDirectory();
	try {
		const link = path.join(root, "outside-link");
		try {
			fs.symlinkSync(outside, link, process.platform === "win32" ? "junction" : "dir");
		} catch (error) {
			t.skip(`symbolic links are unavailable: ${error.message}`);
			return;
		}
		const target = path.join(link, "new.txt");
		const decision = classifyToolCall({ toolName: "write", input: { path: target } }, root);
		assert.equal(decision.action, "block");
		assert.equal(isPathInsideWorkspace(target, root), false);
	} finally {
		fs.rmSync(root, { recursive: true, force: true });
		fs.rmSync(outside, { recursive: true, force: true });
	}
});

test("outside attachments are readable only through their task-scoped staging root", () => {
	const workspace = temporaryDirectory();
	const attachmentRoot = temporaryDirectory();
	const other = temporaryDirectory();
	try {
		const attachment = path.join(attachmentRoot, "run", "image.png");
		fs.mkdirSync(path.dirname(attachment), { recursive: true });
		fs.writeFileSync(attachment, "image");
		assert.equal(classifyToolCall(
			{ toolName: "read", input: { path: attachment } },
			workspace,
			[attachmentRoot],
		).action, "allow");
		assert.equal(classifyToolCall(
			{ toolName: "write", input: { path: attachment } },
			workspace,
			[attachmentRoot],
		).action, "block");
		assert.equal(classifyToolCall(
			{ toolName: "read", input: { path: path.join(other, "secret.txt") } },
			workspace,
			[attachmentRoot],
		).action, "block");
	} finally {
		fs.rmSync(workspace, { recursive: true, force: true });
		fs.rmSync(attachmentRoot, { recursive: true, force: true });
		fs.rmSync(other, { recursive: true, force: true });
	}
});

test("effective skill packages are readable without approval but remain immutable", (t) => {
	const workspace = temporaryDirectory();
	const skillRoot = temporaryDirectory();
	const outside = temporaryDirectory();
	try {
		const skillFile = path.join(skillRoot, "SKILL.md");
		const reference = path.join(skillRoot, "references", "guide.md");
		const directSkill = path.join(outside, "standalone.md");
		fs.mkdirSync(path.dirname(reference), { recursive: true });
		fs.writeFileSync(skillFile, "skill", "utf8");
		fs.writeFileSync(reference, "reference", "utf8");
		fs.writeFileSync(directSkill, "direct", "utf8");

		for (const toolName of ["read", "grep", "find", "ls"]) {
			const decision = classifyToolCall(
				{ toolName, input: { path: toolName === "read" ? reference : skillRoot } },
				workspace,
				[],
				[skillRoot],
				[directSkill],
			);
			assert.equal(decision.action, "allow");
			assert.equal(decision.permissionClass, `${toolName}:read-only-skill`);
		}
		assert.equal(classifyToolCall(
			{ toolName: "read", input: { path: directSkill } },
			workspace,
			[],
			[skillRoot],
			[directSkill],
		).action, "allow");
		assert.equal(classifyToolCall(
			{ toolName: "read", input: { path: path.join(outside, "sibling.md") } },
			workspace,
			[],
			[skillRoot],
			[directSkill],
		).action, "block");
		for (const toolName of ["edit", "write"]) {
			assert.equal(classifyToolCall(
				{ toolName, input: { path: reference } },
				workspace,
				[],
				[skillRoot],
				[directSkill],
			).action, "block");
		}

		const workspaceSkillRoot = path.join(workspace, ".pi", "skills", "local");
		const workspaceSkillFile = path.join(workspaceSkillRoot, "SKILL.md");
		fs.mkdirSync(workspaceSkillRoot, { recursive: true });
		fs.writeFileSync(workspaceSkillFile, "local", "utf8");
		assert.equal(classifyToolCall(
			{ toolName: "write", input: { path: workspaceSkillFile } },
			workspace,
			[],
			[workspaceSkillRoot],
		).action, "block");

		const escapeLink = path.join(skillRoot, "outside-link");
		try {
			fs.symlinkSync(outside, escapeLink, process.platform === "win32" ? "junction" : "dir");
		} catch (error) {
			t.diagnostic(`symbolic-link escape check skipped: ${error.message}`);
			return;
		}
		assert.equal(classifyToolCall(
			{ toolName: "read", input: { path: path.join(escapeLink, "secret.md") } },
			workspace,
			[],
			[skillRoot],
		).action, "block");
	} finally {
		fs.rmSync(workspace, { recursive: true, force: true });
		fs.rmSync(skillRoot, { recursive: true, force: true });
		fs.rmSync(outside, { recursive: true, force: true });
	}
});

test("schema 3 skill access is applied without opening the approval UI", async () => {
	const workspace = temporaryDirectory();
	const skillRoot = temporaryDirectory();
	try {
		const contextFile = path.join(workspace, "active-context.json");
		const skillFile = path.join(skillRoot, "SKILL.md");
		fs.writeFileSync(skillFile, "skill", "utf8");
		const { handlers } = loadExtension(workspace);
		process.env.PI_COMPANION_CONTEXT_FILE = contextFile;
		fs.writeFileSync(contextFile, JSON.stringify({
			schemaVersion: 3,
			generation: 1,
			taskId: "11111111-1111-4111-8111-111111111111",
			runId: "22222222-2222-4222-8222-222222222222",
			workingDirectory: workspace,
			permissionMode: "standard",
			permissionToken: "permission-token-skill-read",
			readOnlyRoots: [],
			skillReadOnlyRoots: [skillRoot],
			skillReadOnlyFiles: [],
			scopeKind: "Workspace",
		}), "utf8");
		const { context } = uiContext(async () => {
			throw new Error("reading an effective skill must not request approval");
		});

		assert.equal(await handlers.get("tool_call")(
			{ toolName: "read", toolCallId: "read-skill", input: { path: skillFile } },
			context,
		), undefined);
		assert.equal((await handlers.get("tool_call")(
			{ toolName: "write", toolCallId: "write-skill", input: { path: skillFile } },
			context,
		)).block, true);
	} finally {
		delete process.env.PI_COMPANION_CONTEXT_FILE;
		fs.rmSync(workspace, { recursive: true, force: true });
		fs.rmSync(skillRoot, { recursive: true, force: true });
	}
});

test("list_available_skills reports effective skills and explains trust exclusions", async () => {
	const workspace = temporaryDirectory();
	const skillRoot = temporaryDirectory();
	try {
		const contextFile = path.join(workspace, "active-context.json");
		fs.writeFileSync(
			path.join(skillRoot, "SKILL.md"),
			"---\nname: find-skills\ndescription: Discover useful agent skills.\n---\n",
			"utf8");
		const { handlers, tools } = loadExtension(workspace);
		process.env.PI_COMPANION_CONTEXT_FILE = contextFile;
		fs.writeFileSync(contextFile, JSON.stringify({
			schemaVersion: 4,
			generation: 1,
			taskId: "11111111-1111-4111-8111-111111111111",
			runId: "22222222-2222-4222-8222-222222222222",
			workingDirectory: workspace,
			permissionMode: "standard",
			permissionToken: "permission-token-skill-list",
			readOnlyRoots: [],
			skillReadOnlyRoots: [skillRoot],
			skillReadOnlyFiles: [],
			scopeKind: "Workspace",
			workspaceTrustStatus: "undecided",
		}), "utf8");

		const result = await tools.get("list_available_skills").execute("list-skills", {});
		assert.match(result.content[0].text, /find-skills: Discover useful agent skills\./u);
		assert.match(result.content[0].text, /workspace is not trusted by Pi/u);
		assert.match(result.content[0].text, /project skills are excluded/u);
		assert.doesNotMatch(result.content[0].text, new RegExp(skillRoot.replaceAll("\\", "\\\\"), "u"));
		assert.equal(classifyToolCall(
			{ toolName: "list_available_skills", input: {} },
			workspace,
		).action, "allow");

		const { context } = uiContext(async () => {
			throw new Error("listing effective skills must not request approval");
		});
		assert.equal(await handlers.get("tool_call")(
			{ toolName: "list_available_skills", toolCallId: "list-skills", input: {} },
			context,
		), undefined);
	} finally {
		delete process.env.PI_COMPANION_CONTEXT_FILE;
		fs.rmSync(workspace, { recursive: true, force: true });
		fs.rmSync(skillRoot, { recursive: true, force: true });
	}
});

test("general chat blocks shell and publishes immutable workspace files", async () => {
	const root = temporaryDirectory();
	const artifacts = temporaryDirectory();
	try {
		process.env.PI_COMPANION_SCOPE_KIND = "GeneralChat";
		process.env.PI_COMPANION_ARTIFACT_DIRECTORY = artifacts;
		const source = path.join(root, "report.csv");
		fs.writeFileSync(source, "name,total\nAda,42\n", "utf8");
		const { handlers, tools } = loadExtension(root, undefined, undefined, "workspace");
		const context = uiContext(async () => {
			throw new Error("general chat shell should fail without prompting");
		}).context;

		const shell = await handlers.get("tool_call")(
			{ toolName: "bash", toolCallId: "bash-general", input: { command: "dir" } },
			context,
		);
		assert.equal(shell.block, true);
		assert.match(shell.reason, /General Chat/u);

		assert.equal(await handlers.get("tool_call")(
			{ toolName: "publish_artifact", toolCallId: "publish-1", input: { path: source } },
			context,
		), undefined);
		const result = await tools.get("publish_artifact").execute(
			"publish-1",
			{ path: source, displayName: "clean-report.csv" },
		);
		assert.equal(result.isError, false);
		assert.equal(result.details.artifact.displayName, "clean-report.csv");
		assert.equal(fs.readFileSync(result.details.artifact.path, "utf8"), "name,total\nAda,42\n");

		const outside = await tools.get("publish_artifact").execute(
			"publish-outside",
			{ path: path.join(artifacts, "outside.txt") },
		);
		assert.equal(outside.isError, true);
	} finally {
		delete process.env.PI_COMPANION_SCOPE_KIND;
		delete process.env.PI_COMPANION_ARTIFACT_DIRECTORY;
		fs.rmSync(root, { recursive: true, force: true });
		fs.rmSync(artifacts, { recursive: true, force: true });
	}
});

test("shell approval can be denied or granted for the task without another prompt", async () => {
	const root = temporaryDirectory();
	try {
		const { handlers, backupDirectory, grantDirectory } = loadExtension(root);
		const handler = handlers.get("tool_call");
		let calls = 0;
		const denied = uiContext(async () => {
			calls += 1;
			return permissionChoices.deny;
		});
		const deniedResult = await handler(
			{ toolName: "bash", toolCallId: "bash-1", input: { command: "dotnet test" } },
			denied.context,
		);
		assert.equal(deniedResult.block, true);

		const allowed = uiContext(async () => {
			calls += 1;
			return permissionChoices.allowTask;
		});
		assert.equal(await handler(
			{ toolName: "bash", toolCallId: "bash-2", input: { command: "dotnet test" } },
			allowed.context,
		), undefined);
		assert.equal(await handler(
			{ toolName: "bash", toolCallId: "bash-3", input: { command: "dotnet test" } },
			allowed.context,
		), undefined);
		assert.equal(calls, 2);
		const differentCommand = uiContext(async () => {
			calls += 1;
			return permissionChoices.deny;
		});
		assert.equal((await handler(
			{ toolName: "bash", toolCallId: "bash-different", input: { command: "npm test" } },
			differentCommand.context,
		)).block, true);
		assert.equal(calls, 3);

		const restarted = loadExtension(root, backupDirectory, grantDirectory);
		const restartedUi = uiContext(async () => {
			throw new Error("persisted task grant should avoid a second prompt");
		});
		assert.equal(await restarted.handlers.get("tool_call")(
			{ toolName: "bash", toolCallId: "bash-4", input: { command: "dotnet test" } },
			restartedUi.context,
		), undefined);
	} finally {
		fs.rmSync(root, { recursive: true, force: true });
	}
});

test("shell approval shows the complete command without silently truncating its tail", async () => {
	const root = temporaryDirectory();
	try {
		const { handlers } = loadExtension(root);
		const command = `node -e "${"x".repeat(2400)}" --visible-tail`;
		let approvalPrompt = "";
		const { context } = uiContext(async (prompt) => {
			approvalPrompt = prompt;
			return permissionChoices.deny;
		});

		const result = await handlers.get("tool_call")(
			{ toolName: "bash", toolCallId: "bash-long-command", input: { command } },
			context,
		);

		assert.equal(result.block, true);
		assert.match(approvalPrompt, /--visible-tail/u);
		assert.match(approvalPrompt, new RegExp(command.replace(/[.*+?^${}()|[\]\\]/gu, "\\$&"), "u"));
	} finally {
		fs.rmSync(root, { recursive: true, force: true });
	}
});

test("workspace edits create a content-addressed backup before execution", async () => {
	const root = temporaryDirectory();
	try {
		const target = path.join(root, "source.txt");
		fs.writeFileSync(target, "original", "utf8");
		const { handlers, backupDirectory } = loadExtension(root);
		const { context } = uiContext(async () => permissionChoices.allowOnce);
		const result = await handlers.get("tool_call")(
			{ toolName: "edit", toolCallId: "edit-1", input: { path: target } },
			context,
		);
		assert.equal(result, undefined);
		const manifest = path.join(backupDirectory, "manifests", "test-run.jsonl");
		const record = JSON.parse(fs.readFileSync(manifest, "utf8").trim());
		assert.equal(record.originalPath, fs.realpathSync.native(target));
		assert.equal(record.toolCallId, "edit-1");
		assert.equal(record.existed, true);
		assert.equal(fs.readFileSync(path.join(backupDirectory, "objects", record.sha256.slice(0, 2), record.sha256), "utf8"), "original");
	} finally {
		fs.rmSync(root, { recursive: true, force: true });
	}
});

test("permission modes enforce read-only, standard, and full access semantics", async () => {
	const root = temporaryDirectory();
	const outside = temporaryDirectory();
	try {
		const target = path.join(root, "source.txt");
		const outsideTarget = path.join(outside, "outside.txt");
		const outsideSibling = path.join(outside, "sibling.txt");
		fs.writeFileSync(target, "original", "utf8");
		fs.writeFileSync(outsideTarget, "outside original", "utf8");
		fs.writeFileSync(outsideSibling, "sibling original", "utf8");

		const readOnly = loadExtension(
			root,
			path.join(root, "read-only-backups"),
			path.join(root, "read-only-grants"),
			"read-only",
		);
		const readOnlyUi = uiContext(async () => {
			throw new Error("read-only mode should block without prompting");
		});
		assert.equal((await readOnly.handlers.get("tool_call")(
			{ toolName: "edit", toolCallId: "edit-read-only", input: { path: target } },
			readOnlyUi.context,
		)).block, true);
		assert.equal((await readOnly.handlers.get("tool_call")(
			{ toolName: "custom_tool", toolCallId: "custom-read-only", input: {} },
			readOnlyUi.context,
		)).block, true);

		const standard = loadExtension(
			root,
			path.join(root, "standard-backups"),
			path.join(root, "standard-grants"),
			"standard",
		);
		const standardUi = uiContext(async () => {
			throw new Error("standard access should allow normal workspace edits without prompting");
		});
		assert.equal(await standard.handlers.get("tool_call")(
			{ toolName: "edit", toolCallId: "edit-standard", input: { path: target } },
			standardUi.context,
		), undefined);

		let outsidePrompt = "";
		const deniedOutsideUi = uiContext(async (prompt) => {
			outsidePrompt = prompt;
			return permissionChoices.deny;
		});
		assert.equal((await standard.handlers.get("tool_call")(
			{ toolName: "read", toolCallId: "read-outside", input: { path: outsideTarget } },
			deniedOutsideUi.context,
		)).block, true);
		assert.match(outsidePrompt, /工作区外访问请求/u);
		const canonicalOutsideTarget = resolveToolTarget(outsideTarget, root);
		assert.match(outsidePrompt, new RegExp(canonicalOutsideTarget.replace(/[.*+?^${}()|[\]\\]/gu, "\\$&"), "u"));

		const allowedOutsideUi = uiContext(async () => permissionChoices.allowTask);
		assert.equal(await standard.handlers.get("tool_call")(
			{ toolName: "edit", toolCallId: "edit-outside-standard", input: { path: outsideTarget } },
			allowedOutsideUi.context,
		), undefined);
		let siblingPromptCount = 0;
		const siblingOutsideUi = uiContext(async () => {
			siblingPromptCount += 1;
			return permissionChoices.deny;
		});
		assert.equal((await standard.handlers.get("tool_call")(
			{ toolName: "edit", toolCallId: "edit-outside-sibling", input: { path: outsideSibling } },
			siblingOutsideUi.context,
		)).block, true);
		assert.equal(siblingPromptCount, 1);

		const fullAccess = loadExtension(
			root,
			path.join(root, "full-access-backups"),
			path.join(root, "full-access-grants"),
			"full-access",
		);
		const fullAccessUi = uiContext(async () => {
			throw new Error("full access should not prompt");
		});
		assert.equal(await fullAccess.handlers.get("tool_call")(
			{ toolName: "edit", toolCallId: "edit-outside", input: { path: outsideTarget } },
			fullAccessUi.context,
		), undefined);
		assert.equal(await fullAccess.handlers.get("tool_call")(
			{ toolName: "bash", toolCallId: "bash-full-access", input: { command: "Remove-Item outside.txt" } },
			fullAccessUi.context,
		), undefined);
		assert.equal(await fullAccess.handlers.get("tool_call")(
			{ toolName: "custom_tool", toolCallId: "custom-full-access", input: {} },
			fullAccessUi.context,
		), undefined);
		const fullAccessManifest = path.join(
			root,
			"full-access-backups",
			"manifests",
			"test-run.jsonl");
		const fullAccessRecord = JSON.parse(fs.readFileSync(fullAccessManifest, "utf8").trim());
		assert.equal(fullAccessRecord.originalPath, fs.realpathSync.native(outsideTarget));
	} finally {
		fs.rmSync(root, { recursive: true, force: true });
		fs.rmSync(outside, { recursive: true, force: true });
	}
});

test("warm runtime reads the active run id before creating each backup", async () => {
	const root = temporaryDirectory();
	try {
		const target = path.join(root, "source.txt");
		const runIdFile = path.join(root, "active-run.txt");
		const activeRunId = "75eb42e9-bc38-48eb-a7fa-e146fe740b90";
		fs.writeFileSync(target, "original", "utf8");
		const { handlers, backupDirectory } = loadExtension(root);
		process.env.PI_COMPANION_RUN_ID_FILE = runIdFile;
		fs.writeFileSync(runIdFile, activeRunId, "utf8");
		const { context } = uiContext(async () => permissionChoices.allowOnce);

		assert.equal(await handlers.get("tool_call")(
			{ toolName: "edit", toolCallId: "edit-warm", input: { path: target } },
			context,
		), undefined);
		assert.equal(fs.existsSync(path.join(backupDirectory, "manifests", `${activeRunId}.jsonl`)), true);
	} finally {
		delete process.env.PI_COMPANION_RUN_ID_FILE;
		fs.rmSync(root, { recursive: true, force: true });
	}
});

test("runtime context switches task grants, permission mode, and backup attribution without reloading", async () => {
	const root = temporaryDirectory();
	const contextFile = path.join(root, "active-context.json");
	const firstTaskId = "11111111-1111-4111-8111-111111111111";
	const secondTaskId = "22222222-2222-4222-8222-222222222222";
	const firstRunId = "33333333-3333-4333-8333-333333333333";
	const secondRunId = "44444444-4444-4444-8444-444444444444";
	try {
		const target = path.join(root, "source.txt");
		fs.writeFileSync(target, "original", "utf8");
		const { handlers, backupDirectory } = loadExtension(root);
		process.env.PI_COMPANION_CONTEXT_FILE = contextFile;
		const writeContext = (taskId, runId, generation, permissionMode = "standard") => fs.writeFileSync(
			contextFile,
			JSON.stringify({
				schemaVersion: 1,
				generation,
				taskId,
				runId,
				workingDirectory: root,
				permissionMode,
				permissionToken: `permission-token-${generation}`,
				readOnlyRoots: [],
			}),
			"utf8",
		);

		writeContext(firstTaskId, firstRunId, 1);
		const firstUi = uiContext(async () => permissionChoices.allowTask);
		assert.equal(await handlers.get("tool_call")(
			{ toolName: "bash", toolCallId: "bash-first", input: { command: "dotnet test" } },
			firstUi.context,
		), undefined);

		writeContext(secondTaskId, secondRunId, 2);
		let secondTaskPrompts = 0;
		const secondUi = uiContext(async () => {
			secondTaskPrompts += 1;
			return permissionChoices.deny;
		});
		assert.equal((await handlers.get("tool_call")(
			{ toolName: "bash", toolCallId: "bash-second", input: { command: "dotnet test" } },
			secondUi.context,
		)).block, true);
		assert.equal(secondTaskPrompts, 1);

		writeContext(secondTaskId, secondRunId, 3, "full-access");
		const writeUi = uiContext(async () => {
			throw new Error("full access mode should allow this edit");
		});
		assert.equal(await handlers.get("tool_call")(
			{ toolName: "edit", toolCallId: "edit-second", input: { path: target } },
			writeUi.context,
		), undefined);
		assert.equal(fs.existsSync(path.join(backupDirectory, "manifests", `${secondRunId}.jsonl`)), true);
		assert.equal(fs.existsSync(path.join(backupDirectory, "manifests", `${firstRunId}.jsonl`)), false);
	} finally {
		delete process.env.PI_COMPANION_CONTEXT_FILE;
		fs.rmSync(root, { recursive: true, force: true });
	}
});

test("invalid runtime context fails closed", async () => {
	const root = temporaryDirectory();
	try {
		const contextFile = path.join(root, "active-context.json");
		const { handlers } = loadExtension(root);
		process.env.PI_COMPANION_CONTEXT_FILE = contextFile;
		fs.writeFileSync(contextFile, "{\"schemaVersion\":99}", "utf8");
		const result = await handlers.get("tool_call")(
			{ toolName: "read", toolCallId: "read-invalid", input: { path: "README.md" } },
			uiContext(async () => permissionChoices.allowOnce).context,
		);
		assert.equal(result.block, true);
		assert.match(result.reason, /运行上下文无效/);
	} finally {
		delete process.env.PI_COMPANION_CONTEXT_FILE;
		fs.rmSync(root, { recursive: true, force: true });
	}
});

test("runtime context blocks a session working-directory mismatch", async () => {
	const root = temporaryDirectory();
	const other = temporaryDirectory();
	try {
		const contextFile = path.join(root, "active-context.json");
		const { handlers } = loadExtension(root);
		process.env.PI_COMPANION_CONTEXT_FILE = contextFile;
		fs.writeFileSync(contextFile, JSON.stringify({
			schemaVersion: 1,
			generation: 1,
			taskId: "11111111-1111-4111-8111-111111111111",
			runId: "22222222-2222-4222-8222-222222222222",
			workingDirectory: root,
			permissionMode: "standard",
			permissionToken: "permission-token-mismatch",
			readOnlyRoots: [],
		}), "utf8");
		const { context } = uiContext(async () => permissionChoices.allowOnce);
		context.cwd = other;
		const result = await handlers.get("tool_call")(
			{ toolName: "read", toolCallId: "read-mismatch", input: { path: "README.md" } },
			context,
		);
		assert.equal(result.block, true);
		assert.match(result.reason, /工作目录与当前 Session 不一致/);
	} finally {
		delete process.env.PI_COMPANION_CONTEXT_FILE;
		fs.rmSync(root, { recursive: true, force: true });
		fs.rmSync(other, { recursive: true, force: true });
	}
});

test("new files create a recoverable non-existent baseline record", async () => {
	const root = temporaryDirectory();
	try {
		const target = path.join(root, "new-file.txt");
		const { handlers, backupDirectory } = loadExtension(root);
		const { context } = uiContext(async () => permissionChoices.allowOnce);
		assert.equal(await handlers.get("tool_call")(
			{ toolName: "write", toolCallId: "write-new", input: { path: target, content: "new" } },
			context,
		), undefined);
		const record = JSON.parse(fs.readFileSync(
			path.join(backupDirectory, "manifests", "test-run.jsonl"),
			"utf8",
		).trim());
		assert.equal(record.originalPath, resolveToolTarget(target, root));
		assert.equal(record.existed, false);
		assert.equal(record.sha256, undefined);
	} finally {
		fs.rmSync(root, { recursive: true, force: true });
	}
});

test("ask_user supports choices and free-form answers", async () => {
	const root = temporaryDirectory();
	try {
		const { tools } = loadExtension(root);
		const askUser = tools.get("ask_user");
		const choiceContext = uiContext(async (_title, options) => options[1]);
		const choiceResult = await askUser.execute(
			"question-1",
			{ question: "选择方向", choices: ["A", "B"], allowOther: false, placeholder: null },
			undefined,
			undefined,
			choiceContext.context,
		);
		assert.equal(choiceResult.content[0].text, "用户回答：B");

		const inputContext = uiContext(async () => undefined);
		inputContext.context.ui.input = async () => "自由回答";
		const inputResult = await askUser.execute(
			"question-2",
			{ question: "补充信息", choices: [], allowOther: false, placeholder: "请输入" },
			undefined,
			undefined,
			inputContext.context,
		);
		assert.equal(inputResult.content[0].text, "用户回答：自由回答");
	} finally {
		fs.rmSync(root, { recursive: true, force: true });
	}
});

test("ask_user can collect a custom answer after selecting other", async () => {
	const root = temporaryDirectory();
	try {
		const { tools } = loadExtension(root);
		const askUser = tools.get("ask_user");
		let presentedChoices;
		const { context } = uiContext(async (_title, options) => {
			presentedChoices = options;
			return "采用自定义方案";
		});
		context.ui.input = async () => assert.fail("custom select answers stay in the same interaction");

		const result = await askUser.execute(
			"question-custom",
			{
				question: "选择方向",
				choices: ["方案 A", "方案 B"],
				allowOther: true,
				placeholder: "写下其他方案",
			},
			undefined,
			undefined,
			context,
		);

		assert.deepEqual(presentedChoices, ["方案 A", "方案 B", "其他…"]);
		assert.equal(result.content[0].text, "用户回答：采用自定义方案");
	} finally {
		fs.rmSync(root, { recursive: true, force: true });
	}
});
