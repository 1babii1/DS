import { nextPlanAction } from './step-up-state.ts'
import assert from 'node:assert/strict'
import { test } from 'node:test'

test('a currency plan asks for re-verification until elevation is active', () => {
	assert.equal(
		nextPlanAction(true, null, new Date('2026-09-30T10:00:00.000Z')),
		'reverify'
	)
	assert.equal(
		nextPlanAction(
			true,
			'2026-09-30T10:05:00.000Z',
			new Date('2026-09-30T10:00:00.000Z')
		),
		'approve'
	)
})

test('a non-sensitive plan remains directly approvable', () => {
	assert.equal(
		nextPlanAction(false, null, new Date('2026-09-30T10:00:00.000Z')),
		'approve'
	)
})
