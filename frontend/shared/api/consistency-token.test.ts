import assert from 'node:assert/strict'
import { test } from 'node:test'

import { consistencyCookieHeader, formatLsn, newerToken, parseLsn, readCookie } from './consistency-token.ts'

test('a position round trips and compares as a number', () => {
	assert.equal(formatLsn(parseLsn('16/B374D848') as bigint), '16/B374D848')
	assert.equal(newerToken('9/0', '10/0'), '10/0')
	assert.equal(newerToken('10/0', '9/0'), '10/0')
})

test('anything that is not a position is ignored', () => {
	for (const bad of [null, undefined, '', '0', '/', '1/', 'G/1', '1/2/3', '100000000/1', '1; DROP/1', '16/B374D848\r\nSet-Cookie: x=1']) {
		assert.equal(parseLsn(bad), null, String(bad))
	}
	assert.equal(newerToken('garbage', '5/5'), '5/5')
	assert.equal(newerToken('5/5', 'garbage'), '5/5')
	assert.equal(newerToken('garbage', 'also'), null)
})

test('the cookie is read by name and written httpOnly, scoped to the proxy and short-lived', () => {
	assert.equal(readCookie('a=1; ds_consistency=16/B374D848; b=2', 'ds_consistency'), '16/B374D848')
	assert.equal(readCookie('a=1', 'ds_consistency'), null)
	assert.equal(readCookie(null, 'ds_consistency'), null)

	const header = consistencyCookieHeader('16/B374D848', true)
	assert.match(header, /HttpOnly/)
	assert.match(header, /Path=\/api\/backend/)
	assert.match(header, /Max-Age=60/)
	assert.match(header, /Secure/)
	assert.doesNotMatch(consistencyCookieHeader('1/1', false), /Secure/)
})
