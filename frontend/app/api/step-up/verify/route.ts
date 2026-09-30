import { auth } from '@/auth'
import { authConfiguration } from '@/shared/auth/config'
import {
	AuthenticationRequiredError,
	StepUpUpstreamError,
	verifyStepUpCode
} from '@/shared/auth/step-up'
import { z } from 'zod'

export const dynamic = 'force-dynamic'

const requestBody = z.object({ code: z.string().trim().min(6).max(128) })

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

	const parsed = requestBody.safeParse(await request.json().catch(() => null))
	if (!parsed.success)
		return Response.json(
			{ detail: 'Enter the confirmation code.' },
			{ status: 400 }
		)

	try {
		await verifyStepUpCode(session.user.id, parsed.data.code)
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
				{ detail: 'Invalid confirmation code.' },
				{ status: 400 }
			)
		}
		return Response.json(
			{ detail: 'Re-verification could not be completed.' },
			{ status: 503 }
		)
	}
}
