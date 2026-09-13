import { isAxiosError } from 'axios'

type EnvelopeMessage = { message?: unknown; fieldName?: unknown }
type Envelope = { error?: { messages?: EnvelopeMessage[] } }

export function applyEnvelopeErrors(error: unknown, setError: (name: 'fullName' | 'email' | 'departmentId' | 'positionId' | 'root', error: { message: string }) => void): boolean {
	if (!isAxiosError(error) || !error.response?.data || typeof error.response.data !== 'object') return false
	const messages = (error.response.data as Envelope).error?.messages
	if (!Array.isArray(messages) || messages.length === 0) return false
	for (const entry of messages) {
		const message = typeof entry.message === 'string' ? entry.message : 'The supplied value is invalid.'
		const field = typeof entry.fieldName === 'string' ? entry.fieldName.replace(/^request\./i, '') : ''
		const name = field ? `${field[0].toLowerCase()}${field.slice(1)}` : 'root'
		setError(['fullName', 'email', 'departmentId', 'positionId'].includes(name) ? name as 'fullName' | 'email' | 'departmentId' | 'positionId' : 'root', { message })
	}
	return true
}
