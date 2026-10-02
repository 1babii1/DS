export function safeReturnTo(
	returnTo: string | undefined,
	applicationUrl: URL
): string {
	if (!returnTo) return '/'

	const target = new URL(returnTo, applicationUrl)
	if (target.origin !== applicationUrl.origin || target.pathname === '/login')
		return '/'

	return `${target.pathname}${target.search}${target.hash}`
}
