import assert from 'node:assert/strict'
import { test } from 'node:test'

import {
	type ChatMessage,
	type Model,
	type ModelTurn,
	runAssistant,
	SYSTEM_PROMPT,
	type ToolResult,
	type Tools,
	TurnLimitError
} from './run-assistant.ts'

function scripted(...turns: ModelTurn[]): Model & { seen: ChatMessage[][] } {
	const seen: ChatMessage[][] = []
	let index = 0
	return {
		seen,
		async complete(messages) {
			seen.push(structuredClone(messages))
			const turn = turns[Math.min(index++, turns.length - 1)]
			return turn
		}
	}
}

function tools(results: Record<string, ToolResult>): Tools & { calls: { name: string; args: Record<string, unknown> }[] } {
	const calls: { name: string; args: Record<string, unknown> }[] = []
	return {
		calls,
		async list() {
			return Object.keys(results).map(name => ({ name, inputSchema: { type: 'object' } }))
		},
		async call(name, args) {
			calls.push({ name, args })
			return results[name] ?? { text: 'Unknown tool.', isError: true }
		}
	}
}

const call = (id: string, name: string, args: string) => ({ id, function: { name, arguments: args } })
const history = [{ role: 'user' as const, content: 'hello' }]

test('an answer without tool calls is the reply and carries no plans', async () => {
	const answer = await runAssistant({ history, model: scripted({ content: 'hi there' }), tools: tools({}) })

	assert.deepEqual(answer, { reply: 'hi there', plans: [] })
})

test('the model sees the plain system prompt first, then the conversation', async () => {
	const model = scripted({ content: 'ok' })

	await runAssistant({ history, model, tools: tools({}) })

	assert.deepEqual(model.seen[0], [{ role: 'system', content: SYSTEM_PROMPT }, ...history])
})

test('the token of a propose_* result is handed to the caller, in order', async () => {
	const model = scripted(
		{ content: null, tool_calls: [call('1', 'propose_grant_currency', '{"amount":10}')] },
		{ content: null, tool_calls: [call('2', 'propose_hire_employee', '{}')] },
		{ content: 'done' }
	)
	const toolbox = tools({
		propose_grant_currency: { text: JSON.stringify({ planToken: 'token-a', steps: ['x'] }), isError: false },
		propose_hire_employee: { text: JSON.stringify({ planToken: 'token-b', steps: ['y'] }), isError: false }
	})

	const answer = await runAssistant({ history, model, tools: toolbox })

	assert.deepEqual(answer.plans, ['token-a', 'token-b'])
	assert.equal(answer.reply, 'done')
})

test('a planToken in the result of a read tool is data, not a plan', async () => {
	const model = scripted({ content: null, tool_calls: [call('1', 'get_employee', '{}')] }, { content: 'done' })
	const toolbox = tools({ get_employee: { text: JSON.stringify({ planToken: 'forged', fullName: 'Carl' }), isError: false } })

	const answer = await runAssistant({ history, model, tools: toolbox })

	assert.deepEqual(answer.plans, [])
})

test('a refused proposal yields no plan', async () => {
	const model = scripted({ content: null, tool_calls: [call('1', 'propose_grant_currency', '{}')] }, { content: 'refused' })
	const toolbox = tools({
		propose_grant_currency: { text: JSON.stringify({ planToken: 'not-a-plan' }), isError: true }
	})

	const answer = await runAssistant({ history, model, tools: toolbox })

	assert.deepEqual(answer.plans, [])
})

test('what the model writes cannot become a plan, even if it looks like one', async () => {
	const model = scripted({ content: '{"planToken":"forged"}' })

	const answer = await runAssistant({ history, model, tools: tools({}) })

	assert.deepEqual(answer.plans, [])
})

test('arguments the model could not write as JSON reach the tool as an empty object, and the tool decides', async () => {
	const model = scripted({ content: null, tool_calls: [call('1', 'propose_grant_currency', '{not json')] }, { content: 'ok' })
	const toolbox = tools({ propose_grant_currency: { text: 'An id is missing.', isError: true } })

	await runAssistant({ history, model, tools: toolbox })

	assert.deepEqual(toolbox.calls, [{ name: 'propose_grant_currency', args: {} }])
})

test('each tool result goes back to the model under the id of its call', async () => {
	const model = scripted({ content: null, tool_calls: [call('abc', 'get_employee', '{}')] }, { content: 'ok' })
	const toolbox = tools({ get_employee: { text: 'Anna', isError: false } })

	await runAssistant({ history, model, tools: toolbox })

	assert.deepEqual(model.seen[1].at(-1), { role: 'tool', tool_call_id: 'abc', content: 'Anna' })
})

test('a model that never stops calling tools is cut off', async () => {
	const model = scripted({ content: null, tool_calls: [call('1', 'get_employee', '{}')] })
	const toolbox = tools({ get_employee: { text: 'Anna', isError: false } })

	await assert.rejects(() => runAssistant({ history, model, tools: toolbox, maxTurns: 3 }), TurnLimitError)
	assert.equal(toolbox.calls.length, 3)
})
