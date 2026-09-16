import { isAxiosError } from 'axios'

type EnvelopeMessage = { message?: unknown; fieldName?: unknown }
type Envelope = { error?: { messages?: EnvelopeMessage[] } }
type FormErrorSetter<TField extends string> = (name: TField | 'root', error: { message: string }) => void

function normalizeFieldName(value: unknown): string {
	if (typeof value !== 'string') return ''
	const field = value.replace(/^(request|locationRequest)\./i, '')
	return field ? `${field[0].toLowerCase()}${field.slice(1)}` : ''
}

export function applyEnvelopeErrors<TField extends string>(
	error: unknown,
	fields: readonly TField[],
	setError: FormErrorSetter<TField>,
): boolean {
	if (!isAxiosError(error) || !error.response?.data || typeof error.response.data !== 'object') return false

	const messages = (error.response.data as Envelope).error?.messages
	if (!Array.isArray(messages) || messages.length === 0) return false

	for (const entry of messages) {
		const message = typeof entry.message === 'string' ? entry.message : 'The supplied value is invalid.'
		const field = normalizeFieldName(entry.fieldName)
		setError(fields.includes(field as TField) ? (field as TField) : 'root', { message })
	}

	return true
}
