import assert from 'node:assert/strict'
import { test } from 'node:test'

import { ModelUnavailableError, openAiModel } from './openai-model.ts'

const base = new URL('http://model.test/')
const tools = [{ name: 'get_employee', description: 'Looks up an employee', inputSchema: { type: 'object' } }]

function replying(body: unknown, status = 200) {
	type Sent = { tools: { type: string; function: unknown }[]; messages: Record<string, unknown>[] }
	const sent: { url: string; body: Sent }[] = []
	const fetcher = (async (url: URL, init: RequestInit) => {
		sent.push({ url: String(url), body: JSON.parse(String(init.body)) })
		return new Response(JSON.stringify(body), { status })
	}) as unknown as typeof fetch
	return { fetcher, sent }
}

test('tools and messages go out in the OpenAI shape, with tool calls typed as functions', async () => {
	const { fetcher, sent } = replying({ choices: [{ message: { content: 'ok' } }] })

	await openAiModel(base, fetcher).complete(
		[
			{ role: 'user', content: 'who?' },
			{ role: 'assistant', content: null, tool_calls: [{ id: 'c1', function: { name: 'get_employee', arguments: '{}' } }] },
			{ role: 'tool', tool_call_id: 'c1', content: 'Anna' }
		],
		tools
	)

	assert.equal(sent[0].url, 'http://model.test/v1/chat/completions')
	assert.deepEqual(sent[0].body.tools[0], {
		type: 'function',
		function: { name: 'get_employee', description: 'Looks up an employee', parameters: { type: 'object' } }
	})
	assert.deepEqual((sent[0].body.messages[1].tool_calls as unknown[])[0], {
		id: 'c1',
		type: 'function',
		function: { name: 'get_employee', arguments: '{}' }
	})
	assert.deepEqual(sent[0].body.messages[2], { role: 'tool', tool_call_id: 'c1', content: 'Anna' })
})

test('the answer and its tool calls come back as a turn', async () => {
	const call = { id: 'c1', function: { name: 'get_employee', arguments: '{"employeeId":"x"}' } }
	const { fetcher } = replying({ choices: [{ message: { content: null, tool_calls: [call] } }] })

	const turn = await openAiModel(base, fetcher).complete([{ role: 'user', content: 'hi' }], tools)

	assert.deepEqual(turn, { content: null, tool_calls: [call] })
})

test('an unreachable model, an error status and an empty answer are all "unavailable"', async () => {
	const down = (async () => {
		throw new TypeError('fetch failed')
	}) as unknown as typeof fetch

	await assert.rejects(() => openAiModel(base, down).complete([], tools), ModelUnavailableError)
	await assert.rejects(() => openAiModel(base, replying({}, 500).fetcher).complete([], tools), ModelUnavailableError)
	await assert.rejects(() => openAiModel(base, replying({ choices: [] }).fetcher).complete([], tools), ModelUnavailableError)
})
