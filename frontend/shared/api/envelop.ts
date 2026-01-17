import { ApiError } from './errors'

export type Envelope<T = unknown> = {
	result: T | null
	errorList: ApiError[] | null
	isError: boolean
	timeGenerated: string
}
