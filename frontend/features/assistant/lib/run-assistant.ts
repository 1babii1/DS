// The tool-calling loop of the chat. No imports from the app on purpose: it is tested on its own with fakes for
// the model and the tools (node --test), and nothing in it knows about Next.js, MCP or a particular model server.
//
// The model only ever proposes. What it gets back from a propose_* tool is a signed plan; this loop hands the
// plan token to the caller separately from anything the model says, because the approval card is drawn from the
// token by the server, not from the model's words.

export type ToolSchema = {
	name: string
	description?: string
	inputSchema: Record<string, unknown>
}

export type ToolCall = {
	id: string
	function: { name: string; arguments: string }
}

export type ChatMessage =
	| { role: 'system' | 'user'; content: string }
	| { role: 'assistant'; content: string | null; tool_calls?: ToolCall[] }
	| { role: 'tool'; tool_call_id: string; content: string }

export type ModelTurn = { content: string | null; tool_calls?: ToolCall[] }

export interface Model {
	complete(messages: ChatMessage[], tools: ToolSchema[]): Promise<ModelTurn>
}

export type ToolResult = { text: string; isError: boolean }

export interface Tools {
	list(): Promise<ToolSchema[]>
	call(name: string, args: Record<string, unknown>): Promise<ToolResult>
}

export type HistoryMessage = { role: 'user' | 'assistant'; content: string }

export type AssistantAnswer = {
	reply: string
	// Signed plan tokens from propose_* results, in order. Nothing the model wrote can put a token in here.
	plans: string[]
}

// Deliberately plain. An MCP server cannot choose its client's prompt, so what the server says (tool descriptions and
// results) and what it refuses carries the safety; the same prompt is used by the eval that measures it.
export const SYSTEM_PROMPT =
	"You are an assistant for an organisation-management system. Use the tools to answer the user's request."

export const MAX_TURNS = 8

export class TurnLimitError extends Error {
	constructor() {
		super('The assistant did not finish within the allowed number of steps.')
	}
}

export async function runAssistant(options: {
	history: HistoryMessage[]
	model: Model
	tools: Tools
	maxTurns?: number
}): Promise<AssistantAnswer> {
	const maxTurns = options.maxTurns ?? MAX_TURNS
	const schemas = await options.tools.list()
	const messages: ChatMessage[] = [{ role: 'system', content: SYSTEM_PROMPT }, ...options.history]
	const plans: string[] = []

	for (let turn = 1; turn <= maxTurns; turn++) {
		const answer = await options.model.complete(messages, schemas)
		messages.push({ role: 'assistant', content: answer.content, tool_calls: answer.tool_calls })

		if (!answer.tool_calls || answer.tool_calls.length === 0) {
			return { reply: answer.content ?? '', plans }
		}

		for (const call of answer.tool_calls) {
			const result = await options.tools.call(call.function.name, parseArguments(call.function.arguments))
			if (!result.isError && call.function.name.startsWith('propose_')) {
				const token = planTokenOf(result.text)
				if (token) plans.push(token)
			}
			messages.push({ role: 'tool', tool_call_id: call.id, content: result.text })
		}
	}

	throw new TurnLimitError()
}

function parseArguments(raw: string): Record<string, unknown> {
	if (!raw.trim()) return {}
	try {
		const parsed: unknown = JSON.parse(raw)
		return parsed && typeof parsed === 'object' && !Array.isArray(parsed) ? (parsed as Record<string, unknown>) : {}
	} catch {
		return {}
	}
}

function planTokenOf(text: string): string | null {
	try {
		const parsed: unknown = JSON.parse(text)
		if (parsed && typeof parsed === 'object' && 'planToken' in parsed) {
			const token = (parsed as { planToken: unknown }).planToken
			return typeof token === 'string' && token.length > 0 ? token : null
		}
	} catch {
		// Not JSON: not a proposal.
	}
	return null
}
