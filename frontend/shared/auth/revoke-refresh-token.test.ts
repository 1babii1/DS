import assert from 'node:assert/strict'
import { test } from 'node:test'

import { revokeRefreshToken } from './revoke-refresh-token.ts'

const client = { issuer: new URL('http://auth.test:5130/'), clientId: 'portfolio-web', clientSecret: 'a-test-secret' }

function recording(status = 200) {
	const calls: { url: string; init: RequestInit }[] = []
	const fetcher = (async (url: URL, init: RequestInit) => {
		calls.push({ url: String(url), init })
		return new Response(null, { status })
	}) as unknown as typeof fetch
	return { fetcher, calls }
}

test('the token goes to the issuer revocation endpoint, as a refresh token, with the client credentials', async () => {
	const { fetcher, calls } = recording()

	assert.equal(await revokeRefreshToken(client, 'r-token', fetcher), true)

	assert.equal(calls[0].url, 'http://auth.test:5130/connect/revocation')
	const body = new URLSearchParams(String(calls[0].init.body))
	assert.equal(body.get('token'), 'r-token')
	assert.equal(body.get('token_type_hint'), 'refresh_token')
	assert.equal(body.get('client_id'), 'portfolio-web')
	assert.equal(body.get('client_secret'), 'a-test-secret')
})

test('a refusal from the issuer is reported, not thrown', async () => {
	assert.equal(await revokeRefreshToken(client, 'r-token', recording(401).fetcher), false)
})

test('an unreachable issuer is reported, not thrown', async () => {
	const down = (async () => { throw new TypeError('fetch failed') }) as unknown as typeof fetch

	assert.equal(await revokeRefreshToken(client, 'r-token', down), false)
})
