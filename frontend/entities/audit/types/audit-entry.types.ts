export type AuditEntry = { id: string; sourceService: string; eventType: string; aggregateId: string; payload: string; occurredAt: string; receivedAt: string }
export type AuditQuery = { aggregateId?: string; page?: number; pageSize?: number }
export type AuditPage = { items: AuditEntry[]; page: number; size: number; total: number; hasNext: boolean }
