import { z } from "zod";

import { auth } from "@/auth";
import { ModelUnavailableError, openAiModel } from "@/features/assistant/lib/openai-model";
import { runAssistant, TurnLimitError } from "@/features/assistant/lib/run-assistant";
import { withMcpTools } from "@/features/assistant/lib/mcp-tools";
import { AuthenticationRequiredError, getAccessToken } from "@/shared/auth/access-token";
import { authConfiguration } from "@/shared/auth/config";

export const dynamic = "force-dynamic";

const modelBaseUrl = new URL(process.env.LLM_BASE_URL || "http://localhost:8090/");

const conversation = z.object({
  messages: z
    .array(z.object({ role: z.enum(["user", "assistant"]), content: z.string().min(1).max(4000) }))
    .min(1)
    .max(30),
});

function hasExpectedOrigin(request: Request): boolean {
  const origin = request.headers.get("origin");
  return !origin || origin === authConfiguration.applicationUrl.origin;
}

// The chat. The model gets the tools McpServer offers and the signed-in person's token; what comes back to the
// browser is its reply and, separately, the signed plan tokens its propose_* calls produced. The browser draws the
// approval card from those tokens through the server, never from the reply.
export async function POST(request: Request): Promise<Response> {
  const session = await auth();
  if (!session?.user?.id) return Response.json({ detail: "Sign in is required." }, { status: 401 });
  if (!hasExpectedOrigin(request)) return Response.json({ detail: "Invalid request origin." }, { status: 403 });

  const parsed = conversation.safeParse(await request.json().catch(() => null));
  if (!parsed.success) return Response.json({ detail: "The message could not be read." }, { status: 400 });

  try {
    const accessToken = await getAccessToken(session.user.id);
    const answer = await withMcpTools(new URL("/mcp", authConfiguration.backendApiOrigin), accessToken, (tools) =>
      runAssistant({ history: parsed.data.messages, model: openAiModel(modelBaseUrl), tools }),
    );
    return Response.json(answer);
  } catch (error) {
    if (error instanceof AuthenticationRequiredError) {
      return Response.json({ detail: "Your session has expired. Sign in again." }, { status: 401 });
    }
    if (error instanceof ModelUnavailableError) {
      return Response.json({ detail: "The assistant is not available right now." }, { status: 503 });
    }
    if (error instanceof TurnLimitError) {
      return Response.json({ detail: error.message }, { status: 504 });
    }
    return Response.json({ detail: "The assistant could not complete the request." }, { status: 502 });
  }
}
