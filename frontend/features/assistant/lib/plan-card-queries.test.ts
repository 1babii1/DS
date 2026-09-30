import {
	affectedPlanQueryKeys,
	assistantPlanKeys
} from './plan-card-queries.ts'
import assert from 'node:assert/strict'
import { test } from 'node:test'

test('a people proposal refreshes employees, audit, and workspace search', () => {
	assert.deepEqual(affectedPlanQueryKeys([{ kind: 'HireEmployee' }]), [
		['employees'],
		['audit'],
		['workspace-search']
	])
})

test('a currency proposal refreshes rewards without dropping the people cache', () => {
	assert.deepEqual(affectedPlanQueryKeys([{ kind: 'GrantCurrency' }]), [
		['rewards'],
		['audit'],
		['workspace-search']
	])
})

test('assistant preview keys are stable and scoped to their signed token', () => {
	assert.deepEqual(assistantPlanKeys.preview('signed-plan-token'), [
		'assistant-plan-preview',
		'signed-plan-token'
	])
})
