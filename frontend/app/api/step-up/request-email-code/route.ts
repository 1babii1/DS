import { auth } from '@/auth'
import { authConfiguration } from '@/shared/auth/config'
import {
	AuthenticationRequiredError,
	requestStepUpEmailCode,
	StepUpUpstreamError
} from '@/shared/auth/step-up'

export const dynamic = 'force-dynamic'

function hasExpectedOrigin(request: Request): boolean {
	const origin = request.headers.get('origin')
	return !origin || origin === authConfiguration.applicationUrl.origin
}

export async function POST(request: Request): Promise<Response> {
	const session = await auth()
	if (!session?.user?.id)
		return Response.json(
			{ detail: 'Sign in is required.' },
			{ status: 401 }
		)
	if (!hasExpectedOrigin(request))
		return Response.json(
			{ detail: 'Invalid request origin.' },
			{ status: 403 }
		)

	try {
		await requestStepUpEmailCode(session.user.id)
		return new Response(null, { status: 204 })
	} catch (error) {
		if (
			error instanceof AuthenticationRequiredError ||
			(error instanceof StepUpUpstreamError && error.status === 401)
		) {
			return Response.json(
				{ detail: 'Your session has expired. Sign in again.' },
				{ status: 401 }
			)
		}
		if (error instanceof StepUpUpstreamError && error.status === 400) {
			return Response.json(
				{ detail: 'Use the current code from your authenticator app.' },
				{ status: 400 }
			)
		}
		return Response.json(
			{ detail: 'A verification code could not be sent.' },
			{ status: 503 }
		)
	}
}
