import { expect, test } from '@playwright/test'

test('the workspace announces an upstream retry without exposing request details', async ({
	page
}) => {
	await page.goto('/')
	await page
		.getByRole('heading', {
			name: 'People operations, connected by design.'
		})
		.waitFor()
	await page.waitForTimeout(100)
	await page.evaluate(() => {
		window.dispatchEvent(
			new CustomEvent('ds:backpressure', {
				detail: 'The service is busy, retrying…'
			})
		)
	})

	await expect(
		page
			.getByRole('status')
			.filter({ hasText: 'The service is busy, retrying…' })
	).toBeVisible()
})
