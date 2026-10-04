import assert from 'node:assert/strict'
import { test } from 'node:test'
import { renderToStaticMarkup } from 'react-dom/server'

import { PersonCard } from './page.tsx'

const employee = {
	departmentId: 'department', departmentName: 'Engineering', email: 'ava@example.test', fullName: 'Ava Example', hiredAt: '2026-10-04T08:00:00Z', id: 'employee', positionId: 'position', positionName: 'Engineer', provisioningFailureReason: 'Onboarding did not complete in time', status: 'ProvisioningFailed'
}

test('a failed onboarding is visible on every person card', () => {
	const card = renderToStaticMarkup(<PersonCard canEdit={false} employee={employee} onGrant={() => undefined} onTransfer={() => undefined} />)
	assert.match(card, /ProvisioningFailed/)
	assert.match(card, /Onboarding issue/)
	assert.match(card, /Onboarding did not complete in time/)
})

test('a normal person card does not show an onboarding issue', () => {
	const card = renderToStaticMarkup(<PersonCard canEdit={false} employee={{ ...employee, provisioningFailureReason: null, status: 'Active' }} onGrant={() => undefined} onTransfer={() => undefined} />)
	assert.doesNotMatch(card, /Onboarding issue/)
})
