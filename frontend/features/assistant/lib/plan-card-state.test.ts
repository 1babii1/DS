import { describeProposalExpiry } from './plan-card-state.ts'
import assert from 'node:assert/strict'
import { test } from 'node:test'

const now = new Date('2026-09-30T10:00:00.000Z')

test('an expired proposal is never presented as actionable', () => {
	assert.deepEqual(describeProposalExpiry('2026-09-30T09:59:59.000Z', now), {
		expired: true,
		label: 'Expired'
	})
})

test('an active proposal states the remaining review time', () => {
	assert.deepEqual(describeProposalExpiry('2026-09-30T10:03:30.000Z', now), {
		expired: false,
		label: 'Expires in 4 min'
	})
})
