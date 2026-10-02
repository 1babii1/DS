// Asks the issuer to end a refresh token (RFC 7009). Server-side only, with the confidential client's own
// credentials, so the browser never sees the token or the secret. A failure is reported, not thrown: the caller has
// already cleared its own copy of the token, and signing out must not fail because the issuer was unreachable.
export type RevocationClient = { issuer: URL; clientId: string; clientSecret: string }

export async function revokeRefreshToken(
	client: RevocationClient,
	refreshToken: string,
	fetcher: typeof fetch = fetch
): Promise<boolean> {
	try {
		const response = await fetcher(new URL('/connect/revocation', client.issuer), {
			method: 'POST',
			headers: { 'content-type': 'application/x-www-form-urlencoded' },
			body: new URLSearchParams({
				token: refreshToken,
				token_type_hint: 'refresh_token',
				client_id: client.clientId,
				client_secret: client.clientSecret
			}),
			cache: 'no-store',
			signal: AbortSignal.timeout(3000)
		})
		return response.ok
	} catch {
		return false
	}
}
