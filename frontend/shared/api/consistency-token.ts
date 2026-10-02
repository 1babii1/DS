// Read-your-writes across a replica (ADR 0025). A write answers with the position of the database after it
// (X-Consistency-Token); the next read must present it so that the service reads from a replica only once the replica has
// caught up. The BFF keeps the newest one per browser in an httpOnly cookie, so the page needs no code of its own.

export const consistencyHeader = 'x-consistency-token'
export const consistencyCookie = 'ds_consistency'

// How long a token stays useful: replication lag is seconds, not minutes, so the cookie does not need to outlive that.
const maxAgeSeconds = 60

// A Postgres position: two hex numbers of up to eight digits, "16/B374D848". Anything else is not one.
// BigInt literals need a newer compile target than this project uses, so the constants are built.
const thirtyTwo = BigInt(32)
const lowMask = BigInt('0xffffffff')

const lsnPattern = /^[0-9A-Fa-f]{1,8}\/[0-9A-Fa-f]{1,8}$/

export function parseLsn(text: string | null | undefined): bigint | null {
	if (!text || !lsnPattern.test(text)) return null
	const [high, low] = text.split('/')
	return (BigInt(`0x${high}`) << thirtyTwo) | BigInt(`0x${low}`)
}

export function formatLsn(value: bigint): string {
	return `${(value >> thirtyTwo).toString(16).toUpperCase()}/${(value & lowMask).toString(16).toUpperCase()}`
}

// The newer of two tokens, as numbers (text order would put "9/0" after "10/0"). A token that is not a position is ignored.
export function newerToken(current: string | null | undefined, incoming: string | null | undefined): string | null {
	const a = parseLsn(current)
	const b = parseLsn(incoming)
	if (a === null && b === null) return null
	if (a === null) return formatLsn(b as bigint)
	if (b === null) return formatLsn(a)
	return formatLsn(a >= b ? a : b)
}

export function readCookie(cookieHeader: string | null | undefined, name: string): string | null {
	if (!cookieHeader) return null
	for (const part of cookieHeader.split(';')) {
		const [key, ...rest] = part.trim().split('=')
		if (key === name) return rest.join('=') || null
	}
	return null
}

export function consistencyCookieHeader(token: string, secure: boolean): string {
	return `${consistencyCookie}=${token}; Max-Age=${maxAgeSeconds}; Path=/api/backend; HttpOnly; SameSite=Lax${secure ? '; Secure' : ''}`
}
