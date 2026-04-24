import { IMcpToolDefinition } from "../core/interfaces/ICommandHandler.js";
import { JObject } from "../types/index.js";
import { z } from "zod";
import { BaseCommandHandler } from "../core/BaseCommandHandler.js";

/**
 * Built-in MCP tool `unity_execute_code`.
 *
 * Unlike other command handlers that route through Unity's `/command` endpoint,
 * this handler POSTs directly to the Unity Editor `/execute_code` HTTP endpoint
 * via {@link UnityConnection.sendToEndpoint}. The BaseCommandHandler's
 * `sendUnityRequest` helper (which wraps requests into `{command, type, params}`
 * for `/command`) is deliberately bypassed here — `/execute_code` expects a
 * flat `{code}` body.
 *
 * See design §3.2 / requirements R2.1.
 *
 * Tool name: `unity_execute_code` (single tool, no action suffix — HandlerAdapter
 * will split the name on `_` and pass "execute" as the action; we ignore it).
 * Idempotency: `unsafe` (C# code may produce side effects).
 */
export class CodeExecutorHandler extends BaseCommandHandler {
    /**
     * Gets the command prefix for this handler.
     *
     * HandlerAdapter uses this as the MCP tool name (combined with the tool
     * key from `getToolDefinitions`). Because the tool is a single operation
     * and uses the full name `unity_execute_code`, we register exactly one
     * tool entry under that key.
     */
    public get commandPrefix(): string {
        return "unity_execute_code";
    }

    /**
     * Gets the description of this command handler.
     */
    public get description(): string {
        return "Executes C# code in the Unity Editor context";
    }

    /**
     * Executes the Unity `/execute_code` HTTP endpoint via sendToEndpoint.
     *
     * The `action` parameter is ignored — HandlerAdapter derives it from the
     * tool-name split (`unity_execute_code` → "execute") but this handler has
     * a single operation so the value is meaningless.
     */
    protected async executeCommand(_action: string, parameters: JObject): Promise<JObject> {
        if (!this.unityConnection) {
            throw new Error("Unity connection not initialized");
        }

        // Extract the optional `target` routing hint injected by HandlerAdapter
        // (design §3.2). Everything else is forwarded to Unity verbatim.
        let target: string | undefined;
        const body: JObject = {};
        if (parameters) {
            for (const key of Object.keys(parameters)) {
                if (key === 'target') {
                    const v = (parameters as any)[key];
                    if (typeof v === 'string' && v !== '') target = v;
                } else {
                    body[key] = (parameters as any)[key];
                }
            }
        }

        return this.unityConnection.sendToEndpoint(
            "/execute_code",
            body,
            { target, idempotency: 'unsafe' }
        );
    }

    /**
     * Gets the tool definitions supported by this handler.
     *
     * Registers one tool under the key `unity_execute_code` (matching the
     * commandPrefix exactly). HandlerAdapter augments the schema with an
     * optional `target` parameter automatically.
     */
    public getToolDefinitions(): Map<string, IMcpToolDefinition> {
        const tools = new Map<string, IMcpToolDefinition>();

        tools.set("unity_execute_code", {
            description:
                "Execute C# code directly in the Unity Editor context. " +
                "The code is wrapped in a method body; use `return <expr>;` " +
                "to surface a value. Use the `code_execute` prompt for the " +
                "correct code template.",
            parameterSchema: {
                code: z
                    .string()
                    .describe("C# code to execute in the Unity Editor context"),
            },
            annotations: {
                title: "Execute C# Code in Unity",
                readOnlyHint: false,
                destructiveHint: true,
                idempotentHint: false,
                openWorldHint: true,
            },
        });

        return tools;
    }
}
