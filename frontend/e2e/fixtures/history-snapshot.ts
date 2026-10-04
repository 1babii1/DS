import { spawn } from 'node:child_process'
import { Pool } from 'pg'

function sql(remove = false): Promise<string> {
	return new Promise((resolve, reject) => {
		const child = spawn('python3', ['../scripts/org-history/emit-sql.py', ...(remove ? ['--remove'] : [])], { cwd: process.cwd(), stdio: ['ignore', 'pipe', 'pipe'] })
		let output = ''
		let error = ''
		child.stdout.on('data', chunk => { output += String(chunk) })
		child.stderr.on('data', chunk => { error += String(chunk) })
		child.on('error', reject)
		child.on('close', code => code === 0 ? resolve(output) : reject(new Error(`History fixture failed (${code}): ${error}`)))
	})
}

async function apply(remove = false): Promise<void> {
	const databaseUrl = process.env.DATABASE_URL
	if (!databaseUrl) throw new Error('DATABASE_URL is required for the local history fixture.')
	const database = new URL(databaseUrl)
	if (!['localhost', '127.0.0.1', '[::1]'].includes(database.hostname)) throw new Error('History fixture only runs against a loopback database.')
	const pool = new Pool({ connectionString: databaseUrl })
	try { await pool.query(await sql(remove)) } finally { await pool.end() }
}

export const seedHistorySnapshot = () => apply()
export const removeHistorySnapshot = () => apply(true)
