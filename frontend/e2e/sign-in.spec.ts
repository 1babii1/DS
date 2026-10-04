import { expect, test } from '@playwright/test'
import { createAccountFixture, disposeAccountFixture } from './fixtures/auth-account'

test('a confirmed viewer returns to the requested workspace without browser-visible OAuth tokens', async ({ page }) => {
	test.setTimeout(60_000)
	const fixture = await createAccountFixture('viewer')

	try {
		await page.goto('/departments')
		await page.getByRole('link', { name: 'Continue to sign in' }).click()
		await page.getByRole('button', { name: 'Continue to sign in' }).click()
		await expect(
			page.getByRole('heading', { name: 'Welcome back.' })
		).toBeVisible()
		await page.locator('#email').fill(fixture.email)
		await page.locator('#password').fill(fixture.password)
		const viewerCapabilities = page.waitForResponse(
			response =>
				new URL(response.url()).pathname ===
					'/api/session/capabilities' &&
				response.request().method() === 'GET'
		)
		await page.getByRole('button', { name: 'Sign in' }).click()
		await expect(page).toHaveURL(/\/departments$/)
		await expect((await viewerCapabilities).json()).resolves.toEqual({
			canEdit: false
		})
		const departmentMutationRequests: string[] = []
		page.on('request', request => {
			if (
				request.method() === 'POST' &&
				new URL(request.url()).pathname ===
					'/api/backend/api/departments'
			)
				departmentMutationRequests.push(request.url())
		})
		await page.getByRole('button', { name: 'New department' }).click()
		await expect(
			page
				.getByRole('alert')
				.filter({ hasText: 'Your viewer role can browse' })
		).toHaveText(
			'Your viewer role can browse the organization but cannot create departments. Ask an administrator for editor access.'
		)
		await expect.poll(() => departmentMutationRequests).toEqual([])
		await expect
			.poll(() =>
				page.evaluate(() =>
					Object.keys({ ...localStorage, ...sessionStorage }).filter(
						key => /token|oauth|refresh/i.test(key)
					)
				)
			)
			.toEqual([])
	} finally {
		await disposeAccountFixture(fixture).catch(() => undefined)
	}
})
