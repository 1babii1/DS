import type { ChatMessage, Model, ModelTurn, ToolCall, ToolSchema } from './run-assistant.ts'

export class ModelUnavailableError extends Error {
	constructor() {
		super('The assistant model could not be reached.')
	}
}

const REQUEST_TIMEOUT_MS = 120_000

// The OpenAI-compatible chat endpoint that llama.cpp's server speaks. Only the parts the loop needs.
export function openAiModel(baseUrl: URL, fetcher: typeof fetch = fetch): Model {
	return {
		async complete(messages: ChatMessage[], tools: ToolSchema[]): Promise<ModelTurn> {
			let response: Response
			try {
				response = await fetcher(new URL('v1/chat/completions', baseUrl), {
					method: 'POST',
					headers: { 'content-type': 'application/json' },
					body: JSON.stringify({
						model: 'assistant',
						messages: messages.map(toWire),
						tools: tools.map(tool => ({
							type: 'function',
							function: { name: tool.name, description: tool.description ?? tool.name, parameters: tool.inputSchema }
						})),
						temperature: 0.3
					}),
					signal: AbortSignal.timeout(REQUEST_TIMEOUT_MS),
					cache: 'no-store'
				})
			} catch {
				throw new ModelUnavailableError()
			}
			if (!response.ok) throw new ModelUnavailableError()

			const body = (await response.json()) as {
				choices?: { message?: { content?: string | null; tool_calls?: ToolCall[] } }[]
			}
			const message = body.choices?.[0]?.message
			if (!message) throw new ModelUnavailableError()
			return { content: message.content ?? null, tool_calls: message.tool_calls }
		}
	}
}

function toWire(message: ChatMessage) {
	if (message.role !== 'assistant' || !message.tool_calls?.length) {
		return message.role === 'assistant' ? { role: 'assistant', content: message.content ?? '' } : message
	}
	return {
		role: 'assistant',
		content: message.content ?? '',
		tool_calls: message.tool_calls.map(call => ({ id: call.id, type: 'function', function: call.function }))
	}
}
