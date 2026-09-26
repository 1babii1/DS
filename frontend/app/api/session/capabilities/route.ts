import { auth } from '@/auth'
import { AuthenticationRequiredError, getAccessToken } from '@/shared/auth/access-token'
import { authConfiguration } from '@/shared/auth/config'

export const dynamic = 'force-dynamic'

const noStoreHeaders = { 'cache-control': 'no-store' }

function canEdit(value: unknown): boolean {
	if (!value || typeof value !== 'object') return false
	const roles = (value as { role?: unknown }).role
	const values = Array.isArray(roles) ? roles : typeof roles === 'string' ? [roles] : []
	return values.includes('admin') || values.includes('editor')
}

export async function GET() {
	const session = await auth()
	if (!session?.user?.id) return Response.json({ detail: 'Sign in is required.' }, { headers: noStoreHeaders, status: 401 })

	try {
		const accessToken = await getAccessToken(session.user.id)
		const response = await fetch(new URL('/connect/userinfo', authConfiguration.issuer), {
			headers: { authorization: `Bearer ${accessToken}` },
			cache: 'no-store',
			signal: AbortSignal.timeout(5_000)
		})
		if (response.status === 401 || response.status === 403) return Response.json({ detail: 'Sign in is required.' }, { headers: noStoreHeaders, status: 401 })
		if (!response.ok) return Response.json({ detail: 'Capabilities are temporarily unavailable.' }, { headers: noStoreHeaders, status: 503 })
		return Response.json({ canEdit: canEdit(await response.json()) }, { headers: noStoreHeaders })
	} catch (error) {
		if (error instanceof AuthenticationRequiredError) return Response.json({ detail: 'Sign in is required.' }, { headers: noStoreHeaders, status: 401 })
		return Response.json({ detail: 'Capabilities are temporarily unavailable.' }, { headers: noStoreHeaders, status: 503 })
	}
}
