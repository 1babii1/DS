import { expect, test } from '@playwright/test'

import { createAccountFixture, disposeAccountFixture } from './fixtures/auth-account'

test('a hub push through nginx refetches the notification bell without exposing credentials', async ({ page }) => {
	test.setTimeout(60_000)
	const fixture = await createAccountFixture('viewer')
	try {
		await page.goto('/departments')
		await page.getByRole('link', { name: 'Continue to sign in' }).click()
		await page.getByRole('button', { name: 'Continue to sign in' }).click()
		await page.locator('#email').fill(fixture.email)
		await page.locator('#password').fill(fixture.password)
		const ticket = page.waitForResponse(response => new URL(response.url()).pathname === '/api/backend/api/notifications/hub-ticket')
		await page.getByRole('button', { name: 'Sign in' }).click()
		await expect(page).toHaveURL(/\/departments$/)
		await ticket
		const refreshed = page.waitForResponse(response => new URL(response.url()).pathname === '/api/backend/api/notifications/unread-count' && response.request().method() === 'GET')
		await page.evaluate(async () => {
			const response = await fetch('/api/backend/api/notifications/test/push', { method: 'POST' })
			if (!response.ok) throw new Error('Local notification fixture was rejected.')
		})
		await refreshed
		await expect(page.getByRole('button', { name: /Open notifications, 1 unread/ })).toBeVisible()
		await expect.poll(() => page.evaluate(() => performance.getEntriesByType('resource').map(entry => entry.name).filter(url => /access_token|refresh_token/.test(url)))).toEqual([])
	} finally {
		await disposeAccountFixture(fixture).catch(() => undefined)
	}
})
