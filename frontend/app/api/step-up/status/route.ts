import { auth } from '@/auth'
import {
	AuthenticationRequiredError,
	getStepUpStatus,
	StepUpUpstreamError
} from '@/shared/auth/step-up'

export const dynamic = 'force-dynamic'

export async function GET(): Promise<Response> {
	const session = await auth()
	if (!session?.user?.id)
		return Response.json(
			{ detail: 'Sign in is required.' },
			{ status: 401 }
		)

	try {
		return Response.json(await getStepUpStatus(session.user.id))
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
		return Response.json(
			{ detail: 'Re-verification is temporarily unavailable.' },
			{ status: 503 }
		)
	}
}
