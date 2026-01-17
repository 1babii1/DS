export type ApiError = {
	messages: ErrorMessage[]
	type: ErrorType
}

export type ErrorMessage = {
	code: string
	message: string
	invalidFields?: string | null
}

export type ErrorType =
	| 'NONE'
	| 'VALIDATION'
	| 'NOT_FOUND'
	| 'FAILURE'
	| 'CONFLICT'

export class EnvelopeError extends Error {
	public readonly apiError: ApiError
	public readonly type: ErrorType

	constructor(apiError: ApiError) {
		const firstMessage =
			apiError.messages.length > 0
				? apiError.messages[0].message
				: 'Unknown error'
		super(firstMessage)

		this.name = 'EnvelopeError'
		this.apiError = apiError
		this.type = apiError.type

		Object.setPrototypeOf(this, EnvelopeError.prototype)
	}

	get messages(): ErrorMessage[] {
		return this.apiError.messages
	}

	get firstMessage(): string {
		return this.apiError.messages.length > 0
			? this.apiError.messages[0].message
			: 'Unknown error'
	}

	get allMessages(): string[] {
		return this.apiError.messages.map(m => m.message)
	}
}

export function isEnvelopeError(error: unknown): error is EnvelopeError {
	return error instanceof EnvelopeError
}
