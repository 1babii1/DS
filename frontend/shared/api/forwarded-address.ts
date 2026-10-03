function isIpv4(value: string): boolean {
	const parts = value.split('.')
	return (
		parts.length === 4 &&
		parts.every(part => /^\d{1,3}$/.test(part) && Number(part) <= 255)
	)
}

function isIpv6(value: string): boolean {
	if (!/^[0-9a-f:]+$/i.test(value) || value.split('::').length > 2)
		return false
	const parts = value.split('::')
	const groups = parts.flatMap(part => (part ? part.split(':') : []))
	return (
		groups.every(group => /^[0-9a-f]{1,4}$/i.test(group)) &&
		(value.includes('::') ? groups.length < 8 : groups.length === 8)
	)
}

function isIpAddress(value: string): boolean {
	return isIpv4(value) || isIpv6(value)
}

export function appendForwardedFor(
	existing: string | null,
	clientAddress: string | null
): string | null {
	if (!clientAddress) return null
	const clientAddresses = clientAddress
		.split(',')
		.map(address => address.trim())
	if (!clientAddresses.every(isIpAddress)) return null
	if (!existing) return clientAddresses.join(', ')
	const existingAddresses = existing.split(',').map(address => address.trim())
	return existingAddresses.every(isIpAddress)
		? [...existingAddresses, ...clientAddresses].join(', ')
		: clientAddresses.join(', ')
}

export function forwardedForFromIncomingHeaders(headers: Pick<Headers, 'get'>): string | null {
	return appendForwardedFor(
		null,
		headers.get('x-forwarded-for') ?? headers.get('x-real-ip')
	)
}
