import { expect, test } from '@playwright/test'
import { randomUUID } from 'node:crypto'

import { createAccountFixture, disposeAccountFixture } from './fixtures/auth-account'

test('an editor can create a department through the protected BFF', async ({ page }) => {
	test.setTimeout(60_000)
	const fixture = await createAccountFixture('editor')
	const identifier = `e2e-${randomUUID().slice(0, 8)}`
	try {
		await page.goto('/departments')
		await page.getByRole('link', { name: 'Continue to sign in' }).click()
		await page.getByRole('button', { name: 'Continue to sign in' }).click()
		await page.locator('#email').fill(fixture.email)
		await page.locator('#password').fill(fixture.password)
		await page.getByRole('button', { name: 'Sign in' }).click()
		await expect(page).toHaveURL(/\/departments$/)
		await page.getByRole('button', { name: 'New department' }).click()
		await expect(page.getByRole('heading', { name: 'New department' })).toBeVisible()
		await page.getByLabel('Name').fill(`Browser ${identifier}`)
		await page.getByLabel('Identifier').fill(identifier)
		await page.locator('input[type="checkbox"]').first().check()
		const created = page.waitForResponse(response => new URL(response.url()).pathname === '/api/backend/api/departments' && response.request().method() === 'POST')
		await page.getByRole('button', { name: 'Create department' }).click()
		expect((await created).ok()).toBe(true)
		await expect(page.getByRole('status').filter({ hasText: 'Department created.' })).toBeVisible()
	} finally {
		await disposeAccountFixture(fixture).catch(() => undefined)
	}
})
