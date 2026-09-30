export type ProposalExpiry = { expired: boolean; label: string }

export function describeProposalExpiry(
	expiresAt: string,
	now: Date = new Date()
): ProposalExpiry {
	const remainingMilliseconds = new Date(expiresAt).getTime() - now.getTime()
	if (!Number.isFinite(remainingMilliseconds) || remainingMilliseconds <= 0)
		return { expired: true, label: 'Expired' }

	const remainingMinutes = Math.ceil(remainingMilliseconds / 60_000)
	if (remainingMinutes < 60)
		return { expired: false, label: `Expires in ${remainingMinutes} min` }

	const hours = Math.floor(remainingMinutes / 60)
	const minutes = remainingMinutes % 60
	return {
		expired: false,
		label: `Expires in ${hours}h${minutes ? ` ${minutes}m` : ''}`
	}
}
