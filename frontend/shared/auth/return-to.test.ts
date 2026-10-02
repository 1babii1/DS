import { safeReturnTo } from './return-to'
import assert from 'node:assert/strict'
import test from 'node:test'

const applicationUrl = new URL('http://localhost:3000')

test('keeps a local workspace route after sign-in', () => {
	assert.equal(
		safeReturnTo('/departments?view=tree', applicationUrl),
		'/departments?view=tree'
	)
})

test('rejects an external or malformed return target', () => {
	assert.equal(safeReturnTo('https://attacker.example', applicationUrl), '/')
	assert.equal(safeReturnTo('//attacker.example', applicationUrl), '/')
	assert.equal(safeReturnTo('/login', applicationUrl), '/')
})
