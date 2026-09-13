export type Page<T> = { items: T[]; page: number; size: number; total: number; hasNext: boolean }
export type Position = { id: string; name: string; description: string | null; isActive: boolean; createdAt: string; updatedAt: string; departments: { id: string; name: string; identifier: string }[] }
export type Location = { id: string; name: string; timezone: string; street: string; city: string; country: string; isActive: boolean; createdAt: string; updatedAt: string }
export type CatalogQuery = { search?: string; isActive?: boolean; page?: number; size?: number; departmentId?: string }
