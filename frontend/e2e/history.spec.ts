import { expect, test } from '@playwright/test'

import { createAccountFixture, disposeAccountFixture } from './fixtures/auth-account'
import { removeHistorySnapshot, seedHistorySnapshot } from './fixtures/history-snapshot'

test('the seeded organization history renders through the protected BFF', async ({ page }) => {
	test.setTimeout(60_000)
	const fixture = await createAccountFixture('viewer')
	await seedHistorySnapshot()
	try {
		await page.goto('/history')
		await page.getByRole('link', { name: 'Continue to sign in' }).click()
		await page.getByRole('button', { name: 'Continue to sign in' }).click()
		await page.locator('#email').fill(fixture.email)
		await page.locator('#password').fill(fixture.password)
		const chart = page.waitForResponse(response => new URL(response.url()).pathname === '/api/backend/api/audit/org-chart')
		await page.getByRole('button', { name: 'Sign in' }).click()
		await expect(page).toHaveURL(/\/history$/)
		await chart
		await expect(page.getByRole('heading', { name: 'Engineering' })).toBeVisible()
	} finally {
		await removeHistorySnapshot().catch(() => undefined)
		await disposeAccountFixture(fixture).catch(() => undefined)
	}
})
