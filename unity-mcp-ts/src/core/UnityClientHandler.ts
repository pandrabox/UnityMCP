import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { UnityConnection } from "./UnityConnection.js";

/**
 * Registers Unity client management tools with the MCP server.
 * These tools allow listing, selecting, and managing connected Unity instances.
 */
export function registerUnityClientTools(server: McpServer): void {
    const connection = UnityConnection.getInstance();

    // List all connected Unity instances
    server.tool(
        "unity_listClients",
        "Lists all connected Unity projects",
        {},
        async () => {
            const clients = connection.getConnectedClients();

            if (clients.length === 0) {
                return {
                    content: [{
                        type: "text",
                        text: "No Unity projects are currently connected."
                    }]
                };
            }

            const activeId = connection.getActiveClientId();
            const lines: string[] = ["Connected Unity projects:", ""];
            clients.forEach((client, index) => {
                const info = client.info || {};
                const isActive = client.isActive || client.id === activeId;
                lines.push(
                    `${index + 1}. ${isActive ? '> ' : '  '}` +
                    `${info.productName || 'Unknown Project'} [${client.state}]`
                );
                lines.push(`   clientId: ${client.id}`);
                lines.push(`   unity:    ${info.unityVersion || 'Unknown'}`);
                lines.push(`   endpoint: ${info.endpoint || 'Unknown'}`);
                lines.push('');
            });

            // Machine-readable JSON summary appended so callers can parse.
            const summary = {
                activeClientId: activeId,
                clients: clients.map(c => ({
                    clientId: c.id,
                    isActive: c.isActive || c.id === activeId,
                    state: c.state,
                    projectName: c.info?.productName,
                    unityVersion: c.info?.unityVersion,
                    endpoint: c.info?.endpoint,
                    port: c.info?.port,
                })),
            };
            lines.push("---");
            lines.push(JSON.stringify(summary));

            return {
                content: [{
                    type: "text",
                    text: lines.join('\n')
                }]
            };
        }
    );

    // Set active client by clientId or projectName
    server.tool(
        "unity_setActiveClient",
        "Sets the active Unity project. Accepts either a clientId or a projectName " +
        "(exact or substring, case-insensitive).",
        {
            clientId: z
                .string()
                .optional()
                .describe("The clientId (exact) of the client to activate."),
            projectName: z
                .string()
                .optional()
                .describe("A project name (exact or substring, case-insensitive).")
        },
        async (params) => {
            const target = params.clientId ?? params.projectName;
            if (!target) {
                return {
                    isError: true,
                    content: [{
                        type: "text",
                        text: "Error: one of `clientId` or `projectName` is required."
                    }]
                };
            }

            const picked = connection.setActiveClientByTarget(target);
            if (!picked) {
                return {
                    isError: true,
                    content: [{
                        type: "text",
                        text: `Error: no Unity instance matches "${target}"`
                    }]
                };
            }

            return {
                content: [{
                    type: "text",
                    text: `Active client set to ${picked.projectName} (clientId=${picked.id}, endpoint=${picked.endpoint})`
                }]
            };
        }
    );

    // Connect to project by name (alias of setActiveClient with projectName).
    server.tool(
        "unity_connectToProject",
        "Connect to a Unity project by name (alias of unity_setActiveClient with projectName).",
        {
            projectName: z.string().describe("The name of the Unity project to connect to")
        },
        async (params) => {
            const picked = connection.setActiveClientByTarget(params.projectName);
            if (!picked) {
                return {
                    isError: true,
                    content: [{
                        type: "text",
                        text: `Error: no projects found matching "${params.projectName}"`
                    }]
                };
            }
            return {
                content: [{
                    type: "text",
                    text: `Successfully connected to "${picked.projectName}" (${picked.endpoint})`
                }]
            };
        }
    );

    // Get active client info
    server.tool(
        "unity_getActiveClient",
        "Get information about the currently active Unity project",
        {},
        async () => {
            if (!connection.hasConnectedClients()) {
                return {
                    content: [{
                        type: "text",
                        text: "No Unity projects are currently connected."
                    }]
                };
            }

            const activeClientId = connection.getActiveClientId();
            if (!activeClientId) {
                return {
                    content: [{
                        type: "text",
                        text: "No active Unity project is selected."
                    }]
                };
            }

            const clients = connection.getConnectedClients();
            const activeClient = clients.find(c => c.id === activeClientId);

            if (!activeClient) {
                return {
                    content: [{
                        type: "text",
                        text: "Active client information not found."
                    }]
                };
            }

            const info = activeClient.info || {};
            const lines = [
                "Active Unity project:",
                "",
                `Project:       ${info.productName}`,
                `State:         ${activeClient.state}`,
                `Unity Version: ${info.unityVersion || 'Unknown'}`,
                `Endpoint:      ${info.endpoint || 'Unknown'}`,
                `clientId:      ${activeClient.id}`,
            ];

            return {
                content: [{
                    type: "text",
                    text: lines.join('\n')
                }]
            };
        }
    );
}
