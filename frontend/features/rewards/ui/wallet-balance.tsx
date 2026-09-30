'use client'

import { useQuery } from '@tanstack/react-query'
import { Coins } from 'lucide-react'

import { rewardsApi } from '@/entities/rewards/api/rewards.api'
import { getHttpStatus } from '@/shared/api/http-error'

export function WalletBalance({ enabled }: { enabled: boolean }) {
	const wallet = useQuery({ queryKey: ['rewards', 'wallet'], queryFn: rewardsApi.wallet, enabled, staleTime: 30_000 })
	const status = getHttpStatus(wallet.error)

	if (!enabled || status === 401 || status === 403 || wallet.isPending || wallet.error) return null

	return <span aria-label={`${wallet.data.balance} reward credits`} className='wallet-balance'><Coins aria-hidden='true' size={15} />{new Intl.NumberFormat('en-US', { maximumFractionDigits: 2 }).format(wallet.data.balance)} credits</span>
}
