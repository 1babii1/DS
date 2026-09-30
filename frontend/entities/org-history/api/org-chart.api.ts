import { axiosInstance } from '@/shared/api/axiosInstance'
import type { OrgChart } from '../types/org-chart.types'

// `at` is a bare date (yyyy-MM-dd, read as the end of that UTC day) or a full instant; omitted means now.
export const orgChartApi = { at: async (at?: string) => (await axiosInstance.get<OrgChart>('/api/audit/org-chart', { params: at ? { at } : {} })).data }
