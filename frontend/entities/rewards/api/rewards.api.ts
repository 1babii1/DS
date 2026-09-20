import { axiosInstance } from '@/shared/api/axiosInstance'

export type RewardWallet = { balance: number; employeeId: string }

export const rewardsApi = {
	wallet: async () => (await axiosInstance.get<RewardWallet>('/api/rewards/wallet')).data
}
