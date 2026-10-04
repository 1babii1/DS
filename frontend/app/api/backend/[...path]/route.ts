import { auth } from '@/auth'
import {
	consistencyCookie,
	consistencyCookieHeader,
	consistencyHeader,
	newerToken,
	parseLsn,
	readCookie
} from '@/shared/api/consistency-token'
import { forwardedForFromIncomingHeaders } from '@/shared/api/forwarded-address'
import {
	AuthenticationRequiredError,
	getAccessToken
} from '@/shared/auth/access-token'
import { authConfiguration } from '@/shared/auth/config'

export const dynamic = 'force-dynamic'

// The browser can only reach workspace APIs and the two human approval endpoints.
// MCP tools themselves remain server-to-server and never receive a browser request.
const allowedPrefixes = [
	'/api/departments',
	'/api/positions',
	'/api/locations',
	'/api/employees',
	'/api/audit',
	'/api/search',
	'/api/rewards',
	'/api/notifications',
	'/mcp/plans'
]
const forwardedRequestHeaders = ['accept', 'content-type', 'if-match', 'idempotency-key', 'x-min-wallet-version']
const forwardedResponseHeaders = ['content-type', 'location', 'etag', 'retry-after', 'x-wallet-version', 'x-card-consistent']

function isAllowedPath(path: string): boolean {
	return allowedPrefixes.some(
		prefix => path === prefix || path.startsWith(`${prefix}/`)
	)
}

function hasExpectedOrigin(request: Request): boolean {
	const origin = request.headers.get('origin')
	return !origin || origin === authConfiguration.applicationUrl.origin
}

async function proxy(
	request: Request,
	context: { params: Promise<{ path: string[] }> }
): Promise<Response> {
	const session = await auth()
	if (!session?.user?.id)
		return Response.json(
			{ detail: 'Sign in is required.' },
			{ status: 401 }
		)
	if (
		!['GET', 'HEAD'].includes(request.method) &&
		!hasExpectedOrigin(request)
	) {
		return Response.json(
			{ detail: 'Invalid request origin.' },
			{ status: 403 }
		)
	}

	const { path } = await context.params
	const upstreamPath = `/${path.join('/')}`
	if (!isAllowedPath(upstreamPath))
		return Response.json(
			{ detail: 'Route is not available.' },
			{ status: 404 }
		)

	try {
		const accessToken = await getAccessToken(session.user.id)
		const requestHeaders = new Headers()
		for (const header of forwardedRequestHeaders) {
			const value = request.headers.get(header)
			if (value) requestHeaders.set(header, value)
		}
		const forwardedFor = forwardedForFromIncomingHeaders(request.headers)
		if (forwardedFor) requestHeaders.set('x-forwarded-for', forwardedFor)
		requestHeaders.set('authorization', `Bearer ${accessToken}`)

		// Read-your-writes (ADR 0025): present the position of this browser's latest write, if it has one, so a service that
		// reads from a replica waits for it. Re-validated here; the cookie is only ever something this proxy set.
		const knownPosition = readCookie(
			request.headers.get('cookie'),
			consistencyCookie
		)
		if (parseLsn(knownPosition) !== null)
			requestHeaders.set(consistencyHeader, knownPosition as string)

		const response = await fetch(
			new URL(
				`${upstreamPath}${new URL(request.url).search}`,
				authConfiguration.backendApiOrigin
			),
			{
				method: request.method,
				headers: requestHeaders,
				body: ['GET', 'HEAD'].includes(request.method)
					? undefined
					: await request.arrayBuffer(),
				cache: 'no-store'
			}
		)
		const responseHeaders = new Headers()
		for (const header of forwardedResponseHeaders) {
			const value = response.headers.get(header)
			if (value) responseHeaders.set(header, value)
		}
		const newPosition = response.headers.get(consistencyHeader)
		const latest = newerToken(knownPosition, newPosition)
		if (latest !== null && latest !== newerToken(knownPosition, null)) {
			responseHeaders.append(
				'set-cookie',
				consistencyCookieHeader(
					latest,
					authConfiguration.applicationUrl.protocol === 'https:'
				)
			)
		}
		return new Response(response.body, {
			status: response.status,
			headers: responseHeaders
		})
	} catch (error) {
		if (error instanceof AuthenticationRequiredError) {
			return Response.json(
				{ detail: 'Your session has expired. Sign in again.' },
				{ status: 401 }
			)
		}
		return Response.json(
			{ detail: 'The service is temporarily unavailable.' },
			{ status: 503 }
		)
	}
}

export const GET = proxy
export const HEAD = proxy
export const POST = proxy
export const PUT = proxy
export const PATCH = proxy
export const DELETE = proxy
