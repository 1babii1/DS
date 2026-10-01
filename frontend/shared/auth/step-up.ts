import {
	AuthenticationRequiredError,
	getAccessToken,
	refreshAccessToken
} from './access-token'
import { authConfiguration } from './config'
import 'server-only'

export type StepUpStatus = {
	twoFactorEnabled: boolean
	elevatedUntil: string | null
}

export class StepUpUpstreamError extends Error {
	constructor(readonly status: number) {
		super('The authentication service rejected the step-up request.')
	}
}

async function request(
	userId: string,
	path: string,
	init?: RequestInit
): Promise<Response> {
	const accessToken = await getAccessToken(userId)
	const headers = new Headers(init?.headers)
	headers.set('authorization', `Bearer ${accessToken}`)

	const response = await fetch(
		new URL(`/auth/step-up/${path}`, authConfiguration.issuer),
		{
			...init,
			headers,
			cache: 'no-store'
		}
	)
	if (!response.ok) throw new StepUpUpstreamError(response.status)
	return response
}

export async function getStepUpStatus(userId: string): Promise<StepUpStatus> {
	const response = await request(userId, 'status')
	return response.json() as Promise<StepUpStatus>
}

export async function requestStepUpEmailCode(userId: string): Promise<void> {
	await request(userId, 'request-email-code', { method: 'POST' })
}

export async function verifyStepUpCode(
	userId: string,
	code: string
): Promise<void> {
	await request(userId, 'verify', {
		method: 'POST',
		headers: { 'content-type': 'application/json' },
		body: JSON.stringify({ code })
	})

	// Do not return this token. It stays in the Auth.js account record and is only used by
	// later same-origin BFF calls on behalf of this signed-in user.
	await refreshAccessToken(userId)
}

export { AuthenticationRequiredError }
