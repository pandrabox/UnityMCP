import { IMcpToolDefinition } from "../core/interfaces/ICommandHandler.js";
import { JObject } from "../types/index.js";
import { z } from "zod";
import { BaseCommandHandler } from "../core/BaseCommandHandler.js";

const DEFAULT_TIMEOUT_MS = 30_000;
const POLL_INTERVAL_MS = 500;
const INITIAL_WAIT_MS = 800;

/**
 * Provides two MCP tools for Unity script compilation:
 *
 * - compile_scripts: triggers AssetDatabase.Refresh() and waits (polling) until
 *   compilation completes, then returns status + any errors. Call this after
 *   creating or editing .cs project files.
 *
 * - compile_status: read-only status check — returns current isCompiling state
 *   and error list without triggering a recompile.
 */
export class CompileCommandHandler extends BaseCommandHandler {
    public get commandPrefix(): string {
        return "compile";
    }

    public get description(): string {
        return "Trigger and monitor Unity script compilation";
    }

    protected async executeCommand(action: string, parameters: JObject): Promise<JObject> {
        switch (action.toLowerCase()) {
            case "scripts":
                return this.compileAndWait(parameters);
            case "status":
                return this.getStatus(parameters);
            default:
                return {
                    success: false,
                    error: `Unknown action: ${action}. Supported actions: scripts, status`
                };
        }
    }

    public getToolDefinitions(): Map<string, IMcpToolDefinition> {
        const tools = new Map<string, IMcpToolDefinition>();

        tools.set("compile_scripts", {
            description:
                "Trigger Unity script recompilation and wait until it finishes. " +
                "IMPORTANT: Call this after creating or editing any .cs project files " +
                "before calling unity_execute_code or menu_execute. " +
                "Returns: status ('ready'|'errors'), errorCount, errors[], warningCount. " +
                "A domain reload may occur during compilation — this is normal.",
            parameterSchema: {
                timeoutMs: z
                    .number()
                    .optional()
                    .describe(`Maximum wait time in milliseconds (default: ${DEFAULT_TIMEOUT_MS})`)
            },
            annotations: {
                title: "Compile Unity Scripts",
                readOnlyHint: false,
                destructiveHint: false,
                idempotentHint: true,
                openWorldHint: false
            }
        });

        tools.set("compile_status", {
            description:
                "Get current Unity compilation status without triggering a recompile. " +
                "Returns: isCompiling (bool), status ('compiling'|'errors'|'ready'), " +
                "errorCount, errors[], warningCount, message.",
            parameterSchema: {},
            annotations: {
                title: "Get Compilation Status",
                readOnlyHint: true,
                idempotentHint: true,
                openWorldHint: false
            }
        });

        return tools;
    }

    private async compileAndWait(parameters: JObject): Promise<JObject> {
        const timeoutMs = (parameters.timeoutMs as number) ?? DEFAULT_TIMEOUT_MS;

        // Trigger AssetDatabase.Refresh on the Unity side.
        // If scripts changed, Unity will start a domain reload — the connection
        // may drop briefly, which is expected and handled by the retry logic.
        try {
            await this.sendUnityRequest("compile.refresh", {});
        } catch {
            // Domain reload in progress — keep going
        }

        // Give Unity a moment to start compiling before the first poll.
        await sleep(INITIAL_WAIT_MS);

        const deadline = Date.now() + timeoutMs;

        while (Date.now() < deadline) {
            try {
                const status = await this.sendUnityRequest("compile.getStatus", {});
                if (status.isCompiling === false) {
                    return status;
                }
            } catch {
                // Unity still reloading — wait and retry
            }
            await sleep(POLL_INTERVAL_MS);
        }

        return {
            status: "timeout",
            isCompiling: true,
            errorCount: 0,
            warningCount: 0,
            errors: [],
            warnings: [],
            message: `Compilation did not finish within ${timeoutMs}ms. Check Unity Editor for details.`
        };
    }

    private async getStatus(parameters: JObject): Promise<JObject> {
        return this.sendUnityRequest("compile.getStatus", parameters);
    }
}

function sleep(ms: number): Promise<void> {
    return new Promise(resolve => setTimeout(resolve, ms));
}
