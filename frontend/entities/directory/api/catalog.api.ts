import { axiosInstance } from '@/shared/api/axiosInstance'
import type { CatalogQuery, Location, Page, Position } from '../types/catalog.types'
const parameters = (query: CatalogQuery) => ({ page: query.page ?? 1, size: query.size ?? 20, ...(query.search ? { search: query.search } : {}), ...(query.isActive === undefined ? {} : { isActive: query.isActive }), ...(query.departmentId ? { departmentId: query.departmentId } : {}) })
export const directoryCatalogApi = { positions: async (query: CatalogQuery = {}) => (await axiosInstance.get<Page<Position>>('/api/positions', { params: parameters(query) })).data, locations: async (query: CatalogQuery = {}) => (await axiosInstance.get<Page<Location>>('/api/locations', { params: parameters(query) })).data }
