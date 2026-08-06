import crypto from "node:crypto";
import fs from "node:fs";
import net from "node:net";
import path from "node:path";

export const permissionChoices = Object.freeze({
	allowOnce: "允许一次",
	allowTask: "本任务内允许同类操作",
	deny: "拒绝",
});

const pathTools = new Set(["read", "grep", "find", "ls", "edit", "write"]);
const writeTools = new Set(["edit", "write"]);
const readOnlyTools = new Set(["read", "grep", "find", "ls", "ask_user", "list_available_skills", "web_search"]);
const companionMutationTools = new Set(["create_task_template"]);
const permissionModes = new Set(["read-only", "standard", "full-access"]);
const sensitiveNames = new Set([".env", ".git", ".npmrc", ".pypirc", "credentials", "id_rsa", "id_ed25519"]);

function normalizeForComparison(value) {
	const normalizedPath = path.normalize(value);
	const root = path.parse(normalizedPath).root;
	const normalized = normalizedPath.length > root.length
		? normalizedPath.replace(/[\\/]+$/, "")
		: normalizedPath;
	return process.platform === "win32" ? normalized.toLocaleLowerCase("en-US") : normalized;
}

function canonicalizeWithExistingAncestor(candidate) {
	let current = path.resolve(candidate);
	const missing = [];
	while (!fs.existsSync(current)) {
		const parent = path.dirname(current);
		if (parent === current) break;
		missing.unshift(path.basename(current));
		current = parent;
	}

	let canonical = fs.existsSync(current) ? fs.realpathSync.native(current) : current;
	for (const segment of missing) canonical = path.join(canonical, segment);
	return path.normalize(canonical);
}

export function resolveToolTarget(inputPath, workingDirectory) {
	const root = canonicalizeWithExistingAncestor(workingDirectory);
	const requested = typeof inputPath === "string" && inputPath.trim().length > 0
		? inputPath.trim()
		: ".";
	const absolute = path.isAbsolute(requested) ? requested : path.resolve(root, requested);
	return canonicalizeWithExistingAncestor(absolute);
}

export function isPathInsideWorkspace(candidate, workingDirectory) {
	const root = canonicalizeWithExistingAncestor(workingDirectory);
	const target = canonicalizeWithExistingAncestor(candidate);
	const relative = path.relative(normalizeForComparison(root), normalizeForComparison(target));
	return relative === "" || (!relative.startsWith(`..${path.sep}`) && relative !== ".." && !path.isAbsolute(relative));
}

function isPathInsideAnyRoot(candidate, roots) {
	return roots.some((root) => isPathInsideWorkspace(candidate, root));
}

function isExactPath(candidate, files) {
	const target = normalizeForComparison(canonicalizeWithExistingAncestor(candidate));
	return files.some((file) =>
		normalizeForComparison(canonicalizeWithExistingAncestor(file)) === target);
}

function isSensitivePath(target, workingDirectory) {
	const relative = path.relative(workingDirectory, target);
	return relative.split(/[\\/]/).some((segment) => sensitiveNames.has(segment.toLocaleLowerCase("en-US")));
}

function commandRisk(command) {
	if (/\b(rm|del|erase|rmdir|rd)\b|\b(remove-item|clear-content)\b|\bformat\b|\bdiskpart\b/i.test(command)) {
		return "shell-delete";
	}
	if (/\b(sudo|runas|start-process\s+[^\r\n]*-verb\s+runas)\b/i.test(command)) {
		return "shell-elevated";
	}
	if (/\b(curl|wget|invoke-webrequest|irm|iwr)\b/i.test(command)) {
		return "shell-download";
	}
	return "shell";
}

function permissionFingerprint(value) {
	return crypto.createHash("sha256").update(value.trim().replace(/\s+/g, " ")).digest("hex").slice(0, 20);
}

function pathPermissionFingerprint(value) {
	return crypto.createHash("sha256").update(normalizeForComparison(value)).digest("hex").slice(0, 20);
}

function describePermission(toolName, input, target, workingDirectory, token) {
	const permissionMarker = `[PI_COMPANION_PERMISSION:${token}]`;
	if (toolName === "create_task_template") {
		const name = String(input.name ?? "").trim();
		const prompt = String(input.prompt ?? "").trim();
		const target = String(input.targetKind ?? "CurrentContext");
		const targetLabel = target === "GeneralChat"
			? "直接对话"
			: target === "Workspace"
				? input.workspaceId ? "固定工作区" : "工作区（使用时选择）"
				: "不指定";
		const workspace = input.workspaceId ? `（${String(input.workspaceId)}）` : "";
		const preferences = [
			`任务环境：${targetLabel}${workspace}`,
			`模型：${input.model || "不指定"}`,
			`推理等级：${input.thinkingLevel || "不指定"}`,
			`权限：${input.permissionMode || "不指定"}`,
			`固定到首页：${input.isPinned === true ? "是" : "否"}`,
		].join("\n");
		return `${permissionMarker}\n创建任务模板\n\n名称：${name}\n${preferences}\n\n完整内容：\n${prompt}`;
	}
	if (toolName === "bash") {
		return `${permissionMarker}\nShell 命令请求\n\n${String(input.command ?? "")}\n\n工作目录：${workingDirectory}`;
	}
	if (target && !isPathInsideWorkspace(target, workingDirectory)) {
		return `${permissionMarker}\n工作区外${writeTools.has(toolName) ? "修改" : "访问"}请求\n\n工具：${toolName}\n目标：${target}`;
	}
	if (writeTools.has(toolName)) {
		return `${permissionMarker}\n${toolName === "write" ? "覆盖或敏感写入" : "敏感文件修改"}\n\n目标：${target}`;
	}
	return `${permissionMarker}\n自定义工具请求\n\n工具：${toolName}`;
}

function createBackup(target, backupDirectory, runId, toolCallId) {
	if (!backupDirectory) return undefined;
	const existed = fs.existsSync(target);
	if (existed && !fs.statSync(target).isFile()) return undefined;
	const content = existed ? fs.readFileSync(target) : undefined;
	const hash = content ? crypto.createHash("sha256").update(content).digest("hex") : undefined;
	if (content && hash) {
		const objectDirectory = path.join(backupDirectory, "objects", hash.slice(0, 2));
		const objectPath = path.join(objectDirectory, hash);
		fs.mkdirSync(objectDirectory, { recursive: true });
		if (!fs.existsSync(objectPath)) {
			try {
				fs.writeFileSync(objectPath, content, { flag: "wx" });
			} catch (error) {
				if (!(error && typeof error === "object" && error.code === "EEXIST")) throw error;
			}
		}
	}

	const manifestDirectory = path.join(backupDirectory, "manifests");
	fs.mkdirSync(manifestDirectory, { recursive: true });
	const record = {
		runId,
		toolCallId,
		originalPath: target,
		sha256: hash,
		size: content?.length ?? 0,
		existed,
		backedUpAt: new Date().toISOString(),
	};
	fs.appendFileSync(path.join(manifestDirectory, `${runId || "unknown"}.jsonl`), `${JSON.stringify(record)}\n`, "utf8");
	return record;
}

function activeRunId(fallbackRunId) {
	const runIdFile = process.env.PI_COMPANION_RUN_ID_FILE;
	if (!runIdFile) return fallbackRunId;
	try {
		const value = fs.readFileSync(runIdFile, "utf8").trim();
		return /^[a-fA-F0-9]{8}-(?:[a-fA-F0-9]{4}-){3}[a-fA-F0-9]{12}$/.test(value) ? value : fallbackRunId;
	} catch {
		return fallbackRunId;
	}
}

function fallbackRuntimeContext() {
	const fallbackRunId = process.env.PI_COMPANION_RUN_ID || "unknown";
	const readOnlyAttachmentRoot = process.env.PI_COMPANION_READ_ONLY_ATTACHMENT_ROOT || "";
	return {
		valid: true,
		schemaVersion: 0,
		generation: 0,
		taskId: process.env.PI_COMPANION_TASK_ID || "unknown",
		runId: activeRunId(fallbackRunId),
		workingDirectory: canonicalizeWithExistingAncestor(process.env.PI_COMPANION_WORKING_DIRECTORY || process.cwd()),
		permissionMode: permissionModes.has(process.env.PI_COMPANION_PERMISSION_MODE)
			? process.env.PI_COMPANION_PERMISSION_MODE
			: "standard",
		permissionToken: process.env.PI_COMPANION_PERMISSION_TOKEN || "unavailable",
		readOnlyRoots: readOnlyAttachmentRoot ? [canonicalizeWithExistingAncestor(readOnlyAttachmentRoot)] : [],
		skillReadOnlyRoots: [],
		skillReadOnlyFiles: [],
		scopeKind: process.env.PI_COMPANION_SCOPE_KIND || "Workspace",
		workspaceTrustStatus: process.env.PI_COMPANION_WORKSPACE_TRUST_STATUS || "unknown",
	};
}

function loadRuntimeContext() {
	const fallback = fallbackRuntimeContext();
	const contextFile = process.env.PI_COMPANION_CONTEXT_FILE;
	if (!contextFile) return fallback;

	try {
		const context = JSON.parse(fs.readFileSync(contextFile, "utf8"));
		const validIdentifier = (value) => typeof value === "string" &&
			/^[a-fA-F0-9]{8}-(?:[a-fA-F0-9]{4}-){3}[a-fA-F0-9]{12}$/.test(value);
		const workspaceTrustStatuses = new Set(["trusted", "declined", "undecided", "not-applicable"]);
		if (![1, 2, 3, 4].includes(context?.schemaVersion) ||
			!Number.isSafeInteger(context.generation) ||
			context.generation < 1 ||
			!validIdentifier(context.taskId) ||
			!validIdentifier(context.runId) ||
			typeof context.workingDirectory !== "string" ||
			!permissionModes.has(context.permissionMode) ||
			typeof context.permissionToken !== "string" ||
			context.permissionToken.length < 16 ||
			!Array.isArray(context.readOnlyRoots) ||
			context.readOnlyRoots.some((root) => typeof root !== "string") ||
			(context.schemaVersion >= 3 && (
				!Array.isArray(context.skillReadOnlyRoots) ||
				context.skillReadOnlyRoots.some((root) => typeof root !== "string") ||
				!Array.isArray(context.skillReadOnlyFiles) ||
				context.skillReadOnlyFiles.some((file) => typeof file !== "string")
			)) ||
			(context.schemaVersion >= 4 && !workspaceTrustStatuses.has(context.workspaceTrustStatus)) ||
			(context.schemaVersion >= 2 && !["Workspace", "GeneralChat"].includes(context.scopeKind))) {
			return { ...fallback, valid: false };
		}

		return {
			valid: true,
			schemaVersion: context.schemaVersion,
			generation: context.generation,
			taskId: context.taskId,
			runId: context.runId,
			workingDirectory: canonicalizeWithExistingAncestor(context.workingDirectory),
			permissionMode: context.permissionMode,
			permissionToken: context.permissionToken,
			readOnlyRoots: context.readOnlyRoots.map(canonicalizeWithExistingAncestor),
			skillReadOnlyRoots: context.schemaVersion >= 3
				? context.skillReadOnlyRoots.map(canonicalizeWithExistingAncestor)
				: [],
			skillReadOnlyFiles: context.schemaVersion >= 3
				? context.skillReadOnlyFiles.map(canonicalizeWithExistingAncestor)
				: [],
			scopeKind: context.schemaVersion >= 2 ? context.scopeKind : "Workspace",
			workspaceTrustStatus: context.schemaVersion >= 4
				? context.workspaceTrustStatus
				: "unknown",
		};
	} catch {
		return { ...fallback, valid: false };
	}
}

function taskGrantFile(grantDirectory, taskId) {
	if (!grantDirectory || !/^[a-zA-Z0-9-]+$/.test(taskId)) return undefined;
	return path.join(grantDirectory, `${taskId}.json`);
}

function loadTaskGrants(grantDirectory, taskId) {
	const grantFile = taskGrantFile(grantDirectory, taskId);
	if (!grantFile || !fs.existsSync(grantFile)) return new Set();
	try {
		const grants = JSON.parse(fs.readFileSync(grantFile, "utf8"));
		return new Set(Array.isArray(grants) ? grants.filter((item) => typeof item === "string") : []);
	} catch {
		return new Set();
	}
}

function saveTaskGrants(grantDirectory, taskId, grants) {
	const grantFile = taskGrantFile(grantDirectory, taskId);
	if (!grantFile) return;
	fs.mkdirSync(grantDirectory, { recursive: true });
	fs.writeFileSync(grantFile, `${JSON.stringify([...grants].sort())}\n`, "utf8");
}

export function classifyToolCall(
	event,
	workingDirectory,
	readOnlyRoots = [],
	skillReadOnlyRoots = [],
	skillReadOnlyFiles = [],
) {
	const toolName = String(event.toolName ?? "");
	const input = event.input && typeof event.input === "object" ? event.input : {};
	if (toolName === "ask_user") return { action: "allow", permissionClass: "ask_user", target: undefined };
	if (toolName === "list_available_skills") {
		return { action: "allow", permissionClass: "skills:list-effective", target: undefined };
	}
	if (toolName === "web_search") return { action: "allow", permissionClass: "network:web-search", target: undefined };
	if (toolName === "create_task_template") {
		return { action: "ask", permissionClass: "companion:task-template:create", target: undefined };
	}
	if (toolName === "publish_artifact") {
		const target = resolveToolTarget(input.path, workingDirectory);
		return isPathInsideWorkspace(target, workingDirectory)
			? { action: "allow", permissionClass: "publish_artifact", target }
			: { action: "block", permissionClass: "publish_artifact:outside-workspace", target };
	}
	if (toolName === "bash") {
		const command = String(input.command ?? "");
		return {
			action: "ask",
			permissionClass: `${commandRisk(command)}:${permissionFingerprint(command)}`,
			target: undefined,
		};
	}
	if (!pathTools.has(toolName)) return { action: "ask", permissionClass: `custom:${toolName}`, target: undefined };

	const target = resolveToolTarget(input.path, workingDirectory);
	if (isPathInsideAnyRoot(target, skillReadOnlyRoots) || isExactPath(target, skillReadOnlyFiles)) {
		return writeTools.has(toolName)
			? { action: "block", permissionClass: `${toolName}:read-only-skill`, target }
			: { action: "allow", permissionClass: `${toolName}:read-only-skill`, target };
	}
	if (!isPathInsideWorkspace(target, workingDirectory)) {
		if (isPathInsideAnyRoot(target, readOnlyRoots)) {
			return writeTools.has(toolName)
				? { action: "block", permissionClass: `${toolName}:read-only-attachment`, target }
				: { action: "allow", permissionClass: `${toolName}:read-only-attachment`, target };
		}
		return {
			action: "block",
			permissionClass: `${toolName}:outside-workspace:${pathPermissionFingerprint(target)}`,
			target,
		};
	}
	if (!writeTools.has(toolName)) return { action: "allow", permissionClass: `${toolName}:workspace`, target };
	if (isSensitivePath(target, workingDirectory)) {
		return { action: "ask", permissionClass: `write:sensitive:${pathPermissionFingerprint(target)}`, target };
	}
	if (toolName === "write" && fs.existsSync(target)) {
		return { action: "ask", permissionClass: `write:overwrite:${pathPermissionFingerprint(target)}`, target };
	}
	return { action: "allow", permissionClass: "write:workspace", target };
}

function artifactContentType(fileName) {
	const extension = path.extname(fileName).toLocaleLowerCase("en-US");
	return new Map([
		[".txt", "text/plain"],
		[".md", "text/markdown"],
		[".csv", "text/csv"],
		[".json", "application/json"],
		[".html", "text/html"],
		[".svg", "image/svg+xml"],
		[".png", "image/png"],
		[".jpg", "image/jpeg"],
		[".jpeg", "image/jpeg"],
		[".pdf", "application/pdf"],
		[".zip", "application/zip"],
		[".docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document"],
		[".xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"],
		[".pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation"],
	]).get(extension) || "application/octet-stream";
}

function safeArtifactName(requested, source) {
	const fallback = path.basename(source) || "artifact";
	let name = typeof requested === "string" && requested.trim().length > 0
		? path.basename(requested.trim())
		: fallback;
	name = name.replace(/[<>:"/\\|?*\u0000-\u001f]/gu, "_");
	name = name.replace(/[. ]+$/u, "").slice(0, 180);
	return name || "artifact";
}

function toolResult(text, isError = false, details = {}) {
	return {
		content: [{ type: "text", text }],
		details,
		isError,
	};
}

function sendAgentCommand(command, signal) {
	const pipeName = process.env.PI_COMPANION_COMMAND_PIPE || "";
	if (!pipeName) return Promise.reject(new Error("Pi Companion 命令通道不可用。"));
	const pipePath = process.platform === "win32" ? `\\\\.\\pipe\\${pipeName}` : pipeName;
	const payload = Buffer.from(JSON.stringify(command), "utf8");
	if (payload.length === 0 || payload.length > 256 * 1024) {
		return Promise.reject(new Error("任务模板请求过大。"));
	}

	return new Promise((resolve, reject) => {
		let settled = false;
		let expectedLength;
		let received = Buffer.alloc(0);
		const socket = net.createConnection(pipePath);
		const finish = (error, value) => {
			if (settled) return;
			settled = true;
			signal?.removeEventListener("abort", abort);
			socket.destroy();
			if (error) reject(error);
			else resolve(value);
		};
		const abort = () => finish(new Error("任务模板创建已取消。"));
		if (signal?.aborted) {
			abort();
			return;
		}
		signal?.addEventListener("abort", abort, { once: true });
		socket.setTimeout(10_000, () => finish(new Error("任务模板创建请求超时。")));
		socket.on("error", error => finish(error));
		socket.on("connect", () => {
			const header = Buffer.allocUnsafe(4);
			header.writeInt32LE(payload.length, 0);
			socket.write(Buffer.concat([header, payload]));
		});
		socket.on("data", chunk => {
			received = Buffer.concat([received, chunk]);
			if (expectedLength === undefined && received.length >= 4) {
				expectedLength = received.readInt32LE(0);
				if (expectedLength <= 0 || expectedLength > 32 * 1024) {
					finish(new Error("Pi Companion 命令响应长度无效。"));
					return;
				}
			}
			if (expectedLength !== undefined && received.length >= expectedLength + 4) {
				try {
					finish(undefined, JSON.parse(received.subarray(4, expectedLength + 4).toString("utf8")));
				} catch (error) {
					finish(error);
				}
			}
		});
		socket.on("end", () => {
			if (!settled) finish(new Error("Pi Companion 命令通道在返回结果前已关闭。"));
		});
	});
}

function skillMetadata(filePath) {
	try {
		const source = fs.readFileSync(filePath, "utf8").slice(0, 64 * 1024);
		const frontmatter = /^---\s*\r?\n([\s\S]*?)\r?\n---(?:\r?\n|$)/u.exec(source)?.[1] ?? "";
		const readValue = (key) => {
			const value = new RegExp(`^${key}:\\s*(.+)$`, "mu").exec(frontmatter)?.[1]?.trim() ?? "";
			return value.replace(/^(["'])(.*)\1$/u, "$2").trim();
		};
		return {
			name: readValue("name") || (
				path.basename(filePath) === "SKILL.md"
					? path.basename(path.dirname(filePath))
					: path.basename(filePath, path.extname(filePath))
			),
			description: readValue("description"),
		};
	} catch {
		return undefined;
	}
}

function availableSkills(runtimeContext) {
	const files = [
		...runtimeContext.skillReadOnlyRoots.map((root) => path.join(root, "SKILL.md")),
		...runtimeContext.skillReadOnlyFiles,
	];
	const seen = new Set();
	return files
		.map(canonicalizeWithExistingAncestor)
		.filter((file) => {
			const key = normalizeForComparison(file);
			if (seen.has(key)) return false;
			seen.add(key);
			return fs.existsSync(file) && fs.statSync(file).isFile();
		})
		.map(skillMetadata)
		.filter((skill) => skill !== undefined)
		.sort((left, right) => left.name.localeCompare(right.name, "en-US"));
}

function workspaceSkillsExcludedByTrust(runtimeContext) {
	return runtimeContext.scopeKind === "Workspace" &&
		["declined", "undecided"].includes(runtimeContext.workspaceTrustStatus);
}

export default function piCompanionExtension(pi) {
	const backupDirectory = process.env.PI_COMPANION_BACKUP_DIRECTORY || "";
	const grantDirectory = process.env.PI_COMPANION_GRANT_DIRECTORY || "";
	const artifactDirectory = process.env.PI_COMPANION_ARTIFACT_DIRECTORY || "";

	pi.registerTool({
		name: "list_available_skills",
		label: "可用技能",
		description: "List the skills that are currently effective and available to this task. Use this instead of scanning parent skill directories.",
		parameters: {
			type: "object",
			properties: {},
			required: [],
			additionalProperties: false,
		},
		constrainedSampling: { type: "json_schema", strict: "prefer" },
		execute: async () => {
			const runtimeContext = loadRuntimeContext();
			if (!runtimeContext.valid) return toolResult("The Pi Companion runtime context is invalid.", true);
			const skills = availableSkills(runtimeContext);
			const trustNotice = workspaceSkillsExcludedByTrust(runtimeContext)
				? "This workspace is not trusted by Pi, so project skills are excluded. Global skills remain eligible."
				: undefined;
			if (skills.length === 0) {
				return toolResult([
					"No effective skills are available to this task.",
					trustNotice,
				].filter(Boolean).join("\n"));
			}
			return toolResult([
				"Effective skills available to this task:",
				...skills.map((skill) => `- ${skill.name}${skill.description ? `: ${skill.description}` : ""}`),
				trustNotice,
			].filter(Boolean).join("\n"));
		},
	});

	if (artifactDirectory) {
		pi.registerTool({
			name: "publish_artifact",
			label: "返回文件",
			description: "将当前隔离工作区中已经完成的最终文件返回给用户。临时文件不要发布。每个最终文件分别调用一次。",
			parameters: {
				type: "object",
				properties: {
					path: { type: "string", description: "当前工作目录内要返回的文件路径" },
					displayName: { type: ["string", "null"], description: "用户可见文件名；不指定时传 null" },
					description: { type: ["string", "null"], description: "简短文件说明；不指定时传 null" },
				},
				required: ["path", "displayName", "description"],
				additionalProperties: false,
			},
			constrainedSampling: { type: "json_schema", strict: "prefer" },
			execute: async (_toolCallId, params) => {
				try {
					const runtimeContext = loadRuntimeContext();
					if (!runtimeContext.valid || runtimeContext.scopeKind !== "GeneralChat") {
						return toolResult("只有 General Chat 可以从隔离空间返回文件。", true);
					}
					const source = resolveToolTarget(params.path, runtimeContext.workingDirectory);
					if (!isPathInsideWorkspace(source, runtimeContext.workingDirectory) ||
						!fs.existsSync(source) ||
						!fs.statSync(source).isFile()) {
						return toolResult("只能返回当前隔离工作区中已经存在的文件。", true);
					}
					const size = fs.statSync(source).size;
					if (size > 256 * 1024 * 1024) {
						return toolResult("单个返回文件不能超过 256 MB。", true);
					}

					const id = crypto.randomUUID();
					const displayName = safeArtifactName(params.displayName, source);
					const runDirectory = path.join(
						path.resolve(artifactDirectory),
						runtimeContext.runId.replace(/[^a-zA-Z0-9-]/gu, "_"));
					fs.mkdirSync(runDirectory, { recursive: true });
					const destination = path.join(runDirectory, `${id}-${displayName}`);
					fs.copyFileSync(source, destination, fs.constants.COPYFILE_EXCL);
					try {
						fs.chmodSync(destination, 0o444);
					} catch {
						// The immutable snapshot still has a unique path if readonly flags are unavailable.
					}
					const contentType = artifactContentType(displayName);
					return toolResult(
						`已返回文件：${displayName}`,
						false,
						{
							artifact: {
								id,
								path: destination,
								displayName,
								contentType,
								size,
								description: typeof params.description === "string" ? params.description.trim() : "",
							},
						});
				} catch (error) {
					return toolResult(
						`返回文件失败：${error instanceof Error ? error.message : String(error)}`,
						true);
				}
			},
		});
	}

	pi.registerTool({
		name: "create_task_template",
		label: "创建任务模板",
		description: "创建一个可复用的 Pi Companion 任务草稿。仅在用户明确要求保存为模板，或用户已经同意 AI 的模板建议时使用。模板不会自动运行；内容必须独立完整，不得包含密钥、附件或临时执行结果。",
		parameters: {
			type: "object",
			properties: {
				name: { type: "string", minLength: 1, maxLength: 80, description: "简短、可辨认的模板名称" },
				prompt: { type: "string", minLength: 1, maxLength: 100000, description: "套用模板时填入输入区的完整任务草稿" },
				targetKind: { type: "string", enum: ["CurrentContext", "Workspace", "GeneralChat"], description: "模板运行位置；通常使用 CurrentContext" },
				workspaceId: { type: ["string", "null"], description: "仅绑定现有工作区时填写其 GUID，否则传 null" },
				model: { type: ["string", "null"], description: "指定模型；继承用户设置时传 null" },
				thinkingLevel: { type: ["string", "null"], enum: [null, "off", "minimal", "low", "medium", "high", "xhigh", "max"], description: "指定推理等级；继承时传 null" },
				permissionMode: { type: ["string", "null"], enum: [null, "read-only", "standard"], description: "权限偏好；继承时传 null，不能使用完全访问" },
				isPinned: { type: "boolean", description: "是否固定到新任务首页；除非用户明确要求，否则传 false" },
			},
			required: ["name", "prompt", "targetKind", "workspaceId", "model", "thinkingLevel", "permissionMode", "isPinned"],
			additionalProperties: false,
		},
		constrainedSampling: { type: "json_schema", strict: "prefer" },
		execute: async (toolCallId, params, signal) => {
			try {
				const runtimeContext = loadRuntimeContext();
				if (!runtimeContext.valid || runtimeContext.schemaVersion < 1) {
					return toolResult("Pi Companion 运行上下文无效，无法创建任务模板。", true);
				}
				if (!process.env.PI_COMPANION_COMMAND_PIPE) {
					return toolResult("当前版本的 Pi Companion 未提供任务模板命令通道。", true);
				}
				const response = await sendAgentCommand({
					type: "createTaskTemplate",
					requestId: toolCallId,
					taskId: runtimeContext.taskId,
					runId: runtimeContext.runId,
					generation: runtimeContext.generation,
					permissionToken: runtimeContext.permissionToken,
					template: params,
				}, signal);
				if (!response?.success) {
					return toolResult(`创建任务模板失败：${response?.error || "未知错误"}`, true);
				}
				const status = response.alreadyExisted ? "已存在，无需重复创建" : "已创建";
				return toolResult(
					`任务模板“${response.templateName}”${status}。模板只会填入任务草稿，不会自动运行。`,
					false,
					{
						templateId: response.templateId,
						templateName: response.templateName,
						alreadyExisted: response.alreadyExisted === true,
					});
			} catch (error) {
				return toolResult(`创建任务模板失败：${error instanceof Error ? error.message : String(error)}`, true);
			}
		},
	});

	pi.on("tool_call", async (event, ctx) => {
		const runtimeContext = loadRuntimeContext();
		if (!runtimeContext.valid) {
			return { block: true, reason: "Pi Companion 运行上下文无效；为避免权限串用，操作已阻止。" };
		}
		if (typeof ctx.cwd === "string" &&
			normalizeForComparison(canonicalizeWithExistingAncestor(ctx.cwd)) !==
				normalizeForComparison(runtimeContext.workingDirectory)) {
			return { block: true, reason: "Pi Companion 工作目录与当前 Session 不一致；操作已阻止。" };
		}

		const taskGrants = loadTaskGrants(grantDirectory, runtimeContext.taskId);
		const decision = classifyToolCall(
			event,
			runtimeContext.workingDirectory,
			runtimeContext.readOnlyRoots,
			runtimeContext.skillReadOnlyRoots,
			runtimeContext.skillReadOnlyFiles,
		);
		const fullAccess = runtimeContext.scopeKind === "Workspace" &&
			runtimeContext.permissionMode === "full-access";
		const standardAccess = runtimeContext.scopeKind === "Workspace" &&
			runtimeContext.permissionMode === "standard";
		const standardOutsideRequest = standardAccess &&
			pathTools.has(event.toolName) &&
			decision.permissionClass.includes(":outside-workspace:");
		if (runtimeContext.scopeKind === "GeneralChat" && event.toolName === "bash") {
			return { block: true, reason: "General Chat 的隔离空间不允许执行 Shell 命令。" };
		}
		if (runtimeContext.permissionMode === "read-only" &&
			!readOnlyTools.has(event.toolName) &&
			!companionMutationTools.has(event.toolName)) {
			return { block: true, reason: "Pi Companion 当前使用只读权限模式。" };
		}
		if (decision.action === "block" && !fullAccess && !standardOutsideRequest) {
			ctx.ui.notify(`已阻止工作目录外访问：${decision.target ?? event.toolName}`, "warning");
			return { block: true, reason: `Pi Companion 已阻止工作目录外访问：${decision.target ?? event.toolName}` };
		}

		const standardModeAllowsOverwrite = runtimeContext.permissionMode === "standard" &&
			decision.permissionClass.startsWith("write:overwrite:");
		const isCompanionMutation = companionMutationTools.has(event.toolName);
		const requiresConfirmation = isCompanionMutation || (!fullAccess && (
			(decision.action === "ask" && !standardModeAllowsOverwrite) ||
			standardOutsideRequest));
		if (requiresConfirmation && (isCompanionMutation || !taskGrants.has(decision.permissionClass))) {
			if (!ctx.hasUI) return { block: true, reason: "Pi Companion 无可用授权界面，操作已阻止。" };
			const selected = await ctx.ui.select(
				describePermission(
					event.toolName,
					event.input,
					decision.target,
					runtimeContext.workingDirectory,
					runtimeContext.permissionToken),
				isCompanionMutation
					? [permissionChoices.allowOnce, permissionChoices.deny]
					: [permissionChoices.allowOnce, permissionChoices.allowTask, permissionChoices.deny],
			);
			if (selected === permissionChoices.allowTask) {
				taskGrants.add(decision.permissionClass);
				saveTaskGrants(grantDirectory, runtimeContext.taskId, taskGrants);
			}
			if (selected !== permissionChoices.allowOnce && selected !== permissionChoices.allowTask) {
				return { block: true, reason: "用户拒绝了此操作。" };
			}
		}

		if (writeTools.has(event.toolName) && decision.target) {
			try {
				createBackup(decision.target, backupDirectory, runtimeContext.runId, event.toolCallId);
			} catch (error) {
				return { block: true, reason: `修改前备份失败，操作已阻止：${error instanceof Error ? error.message : String(error)}` };
			}
		}
		return undefined;
	});

	pi.registerTool({
		name: "ask_user",
		label: "向用户提问",
		description: "当继续任务需要用户选择或补充信息时使用。choices 非空时显示单选，空数组时显示自由输入；allowOther 可允许用户在单选后改为自由输入。",
		parameters: {
			type: "object",
			properties: {
				question: { type: "string", description: "要向用户提出的具体问题" },
				choices: { type: "array", items: { type: "string" }, maxItems: 8, description: "单选答案；自由输入时必须传空数组 []，不要传 null" },
				allowOther: { type: "boolean", description: "choices 非空时是否追加“其他…”选项，并在选中后请求自由输入" },
				placeholder: { type: ["string", "null"], description: "自由输入提示；不需要时传 null" },
			},
			required: ["question", "choices", "allowOther", "placeholder"],
			additionalProperties: false,
		},
		constrainedSampling: { type: "json_schema", strict: "prefer" },
		execute: async (_toolCallId, params, _signal, _onUpdate, ctx) => {
			if (!ctx.hasUI) return toolResult("当前没有可用的用户交互界面。", true);
			const choices = Array.isArray(params.choices) ? params.choices.filter((item) => typeof item === "string" && item.length > 0) : [];
			const otherChoice = "其他…";
			const selectableChoices = choices.length > 0 && params.allowOther === true && !choices.includes(otherChoice)
				? [...choices, otherChoice]
				: choices;
			const response = selectableChoices.length > 0
				? await ctx.ui.select(params.question, selectableChoices)
				: await ctx.ui.input(params.question, params.placeholder ?? undefined);
			return response === undefined
				? toolResult("用户取消了问题。", true)
				: toolResult(`用户回答：${response}`);
		},
	});
}
