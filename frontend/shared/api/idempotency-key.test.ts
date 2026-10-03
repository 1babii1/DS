import assert from 'node:assert/strict'
import { test } from 'node:test'

import { idempotencyKeyFor } from './idempotency-key.ts'

test('a retry with unchanged submission content keeps its idempotency key', () => {
	const generated = ['first', 'second']
	const create = () => generated.shift() as string
	const first = idempotencyKeyFor(null, { email: 'alex@example.test', fullName: 'Alex', positionId: 'p1' }, create)
	const retry = idempotencyKeyFor(first, { positionId: 'p1', fullName: 'Alex', email: 'alex@example.test' }, create)
	assert.deepEqual(retry, first)
})

test('changed submission content receives a new idempotency key', () => {
	const create = () => 'new-key'
	const next = idempotencyKeyFor({ key: 'old-key', snapshot: '{"email":"alex@example.test"}' }, { email: 'sam@example.test' }, create)
	assert.deepEqual(next, { key: 'new-key', snapshot: '{"email":"sam@example.test"}' })
})
