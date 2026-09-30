import "server-only";

import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StreamableHTTPClientTransport } from "@modelcontextprotocol/sdk/client/streamableHttp.js";

import type { Tools } from "./run-assistant";

// The assistant reaches McpServer as the signed-in person: their access token goes on every request, so what a tool
// can read is what that person could read, and a proposal is bound to them. One short-lived session per chat request.
export async function withMcpTools<T>(mcpUrl: URL, accessToken: string, run: (tools: Tools) => Promise<T>): Promise<T> {
  const client = new Client({ name: "ds-assistant", version: "1.0.0" });
  const transport = new StreamableHTTPClientTransport(mcpUrl, {
    requestInit: { headers: { Authorization: `Bearer ${accessToken}` } },
  });
  await client.connect(transport);

  try {
    return await run({
      async list() {
        const { tools } = await client.listTools();
        return tools.map((tool) => ({
          name: tool.name,
          description: tool.description,
          inputSchema: tool.inputSchema as Record<string, unknown>,
        }));
      },
      async call(name, args) {
        const result = await client.callTool({ name, arguments: args });
        const content = Array.isArray(result.content) ? result.content : [];
        const text = content
          .map((part) => (part && typeof part === "object" && "text" in part ? String(part.text) : ""))
          .join("\n");
        return { text, isError: result.isError === true };
      },
    });
  } finally {
    await client.close();
  }
}
