import assert from 'node:assert/strict'
import { test } from 'node:test'

import { balanceView } from './employee-balance.ts'

const format = (value: number) => String(value)

test('a balance is shown with the time it is as of', () => {
	assert.deepEqual(balanceView({ balance: 120, balanceChangedAt: '2026-10-03T10:00:00Z', consistent: true }, format), { text: '120 credits', asOf: '2026-10-03T10:00:00Z', updating: false })
})

test('no wallet event yet is not the same as a balance of zero', () => {
	assert.equal(balanceView({ balance: null, balanceChangedAt: null, consistent: true }, format).text, 'No balance yet')
	assert.equal(balanceView({ balance: 0, balanceChangedAt: null, consistent: true }, format).text, '0 credits')
})

test('a card behind the grant that was just made is shown as updating, not as current', () => {
	assert.equal(balanceView({ balance: 100, balanceChangedAt: '2026-10-03T10:00:00Z', consistent: false }, format).updating, true)
	assert.equal(balanceView({ balance: null, balanceChangedAt: null, consistent: false }, format).updating, true)
})
