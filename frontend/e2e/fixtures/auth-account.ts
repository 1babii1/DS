import { spawn } from 'node:child_process'
import { randomUUID } from 'node:crypto'
import { join } from 'node:path'

type FixtureRole = 'viewer' | 'editor'
type Fixture = { email: string; password: string }

function runFixture(input: object): Promise<void> {
	return new Promise((resolve, reject) => {
		const runner = spawn('dotnet', ['run', '--project', join('..', 'backend', 'AuthService', 'AuthService.BrowserFixtures', 'AuthService.BrowserFixtures.csproj'), '--no-restore'], {
		cwd: process.cwd(), env: { ...process.env, E2E_AUTH_FIXTURES: '1' }, stdio: ['pipe', 'pipe', 'pipe']
		})
		let stderr = ''
		runner.stderr.on('data', chunk => { stderr += String(chunk) })
		runner.on('error', reject)
		runner.on('close', code => code === 0 ? resolve() : reject(new Error(`Local auth fixture failed (${code}): ${stderr}`)))
		runner.stdin.end(JSON.stringify(input))
	})
}

export async function createAccountFixture(role: FixtureRole): Promise<Fixture> {
	const fixture = { email: `e2e-${randomUUID()}@test.local`, password: `E2eA1-${randomUUID()}` }
	await runFixture({ operation: 'create', ...fixture, role })
	return fixture
}

export async function disposeAccountFixture(fixture: Fixture): Promise<void> {
	await runFixture({ operation: 'dispose', email: fixture.email })
}
