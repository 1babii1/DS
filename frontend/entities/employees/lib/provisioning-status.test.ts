import assert from 'node:assert/strict'
import { test } from 'node:test'

import { needsProvisioningRefresh, provisioningFailureMessage } from './provisioning-status.ts'

test('a failed onboarding exposes its reason instead of only the status', () => {
	assert.equal(
		provisioningFailureMessage({ status: 'ProvisioningFailed', provisioningFailureReason: 'Onboarding did not complete in time' }),
		'Onboarding did not complete in time'
	)
})

test('other statuses and empty failure reasons do not render a misleading detail', () => {
	assert.equal(provisioningFailureMessage({ status: 'Active', provisioningFailureReason: 'irrelevant' }), null)
	assert.equal(provisioningFailureMessage({ status: 'ProvisioningFailed', provisioningFailureReason: null }), null)
})

test('only pending onboarding is refreshed while its eventual outcome is unknown', () => {
	assert.equal(needsProvisioningRefresh([{ status: 'PendingProvisioning' }]), true)
	assert.equal(needsProvisioningRefresh([{ status: 'Active' }, { status: 'ProvisioningFailed' }]), false)
})
