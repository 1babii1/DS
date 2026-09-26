import { axiosInstance } from '@/shared/api/axiosInstance'

export type RewardWallet = { balance: number; employeeId: string }

export const rewardsApi = {
	wallet: async () => (await axiosInstance.get<RewardWallet>('/api/rewards/wallet')).data,
	grant: async (input: { amount: number; employeeId: string; reason: string }) => axiosInstance.post('/api/rewards/grants', input)
}
