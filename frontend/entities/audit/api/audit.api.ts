import { axiosInstance } from '@/shared/api/axiosInstance'
import type { AuditPage, AuditQuery } from '../types/audit-entry.types'
export const auditApi = { list: async (query: AuditQuery = {}) => (await axiosInstance.get<AuditPage>('/api/audit', { params: { page: query.page ?? 1, pageSize: query.pageSize ?? 50, ...(query.aggregateId ? { aggregateId: query.aggregateId } : {}) } })).data }
