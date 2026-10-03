import assert from 'node:assert/strict'
import { test } from 'node:test'

import { cardNeedsAnotherLook, staleCardRetries } from './use-employee-card.ts'

test('a card that came back behind what was asked for is looked at again', () => {
	assert.equal(cardNeedsAnotherLook({ consistent: false }, 1), true)
})

test('a consistent card, or none yet, is not polled', () => {
	assert.equal(cardNeedsAnotherLook({ consistent: true }, 1), false)
	assert.equal(cardNeedsAnotherLook(undefined, 0), false)
})

test('a card that stays behind is given up on after a few looks', () => {
	assert.equal(cardNeedsAnotherLook({ consistent: false }, staleCardRetries), true)
	assert.equal(cardNeedsAnotherLook({ consistent: false }, staleCardRetries + 1), false)
})
