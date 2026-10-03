import { expect, test } from '@playwright/test'
import { randomUUID } from 'node:crypto'

const issuer = process.env.AUTH_OIDC_ISSUER ?? 'http://localhost:5130/'
const mailpitUrl = 'http://localhost:8025'

type MailpitMessage = { ID: string; To: Array<{ Address: string }> }

async function confirmationLink(
	email: string
): Promise<{ id: string; url: string }> {
	for (let attempt = 0; attempt < 20; attempt += 1) {
		const inbox = await fetch(
			`${mailpitUrl}/api/v1/messages?limit=50`
		).then(
			response =>
				response.json() as Promise<{ messages: MailpitMessage[] }>
		)
		const message = inbox.messages.find(candidate =>
			candidate.To.some(recipient => recipient.Address === email)
		)
		if (message) {
			const detail = await fetch(
				`${mailpitUrl}/api/v1/message/${message.ID}`
			).then(response => response.json() as Promise<{ Text: string }>)
			const url = detail.Text.match(
				/http:\/\/localhost:5130\/auth\/email\/confirm\?[^\s]+/
			)?.[0]
			if (url) return { id: message.ID, url: url.replace(/&amp;/g, '&') }
		}

		await new Promise(resolve => setTimeout(resolve, 250))
	}

	throw new Error(
		'Timed out waiting for the test account confirmation email.'
	)
}

test('a confirmed viewer returns to the requested workspace without browser-visible OAuth tokens', async ({
	context,
	page
}) => {
	test.setTimeout(60_000)
	const email = `e2e-${randomUUID()}@test.local`
	const password = `E2eA1-${randomUUID()}`
	let messageId: string | undefined

	try {
		await page.goto('/departments')
		await page.getByRole('link', { name: 'Continue to sign in' }).click()
		await page.getByRole('button', { name: 'Continue to sign in' }).click()
		await expect(
			page.getByRole('heading', { name: 'Welcome back.' })
		).toBeVisible()
		await page
			.getByRole('link', { name: 'Create a viewer account' })
			.click()
		await expect(
			page.getByRole('heading', {
				name: 'Create your workspace account.'
			})
		).toBeVisible()
		await page.locator('#registration-email').fill(email)
		await page.locator('#registration-password').fill(password)
		await page.locator('#registration-confirm-password').fill(password)
		await page
			.getByRole('button', { name: 'Create viewer account' })
			.click()
		await expect(
			page.getByText('Your account is ready. Continue to sign in.')
		).toBeVisible()

		const confirmation = await confirmationLink(email)
		messageId = confirmation.id
		await fetch(confirmation.url)

		await page.goto('/departments')
		await page.getByRole('link', { name: 'Continue to sign in' }).click()
		await page.getByRole('button', { name: 'Continue to sign in' }).click()
		await page.locator('#email').fill(email)
		await page.locator('#password').fill(password)
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
		await context.request
			.delete(new URL('/auth/account', issuer).toString(), {
				data: { password }
			})
			.catch(() => undefined)
		if (messageId)
			await fetch(`${mailpitUrl}/api/v1/messages`, {
				body: JSON.stringify({ IDs: [messageId] }),
				headers: { 'content-type': 'application/json' },
				method: 'DELETE'
			}).catch(() => undefined)
	}
})
