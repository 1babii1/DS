import { expect, test } from '@playwright/test'

test('an anonymous visitor sees a sign-in state for a protected workspace route', async ({
	page
}) => {
	const startedAt = Date.now()
	await page.goto('/departments')

	await expect(
		page.getByRole('heading', { name: 'Sign in to view this workspace' })
	).toBeVisible()
	await expect(
		page.getByRole('link', { name: 'Continue to sign in' })
	).toBeVisible()
	expect(Date.now() - startedAt).toBeLessThan(5_000)
})
