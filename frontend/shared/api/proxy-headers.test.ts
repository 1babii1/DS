import assert from 'node:assert/strict'
import { test } from 'node:test'

import { forwardedRequestHeaders, forwardedResponseHeaders } from './proxy-headers.ts'

test('the wallet version a grant answers with reaches the browser, and the version a card read waits for reaches the backend', () => {
	assert.ok(forwardedResponseHeaders.includes('x-wallet-version'))
	assert.ok(forwardedRequestHeaders.includes('x-min-wallet-version'))
	assert.ok(forwardedResponseHeaders.includes('x-card-consistent'))
})

test('the headers earlier features depend on are still passed', () => {
	for (const header of ['idempotency-key', 'if-match', 'content-type']) assert.ok(forwardedRequestHeaders.includes(header), header)
	for (const header of ['retry-after', 'etag', 'location']) assert.ok(forwardedResponseHeaders.includes(header), header)
})

test('every header is named in lower case, the way the runtime reports them', () => {
	for (const header of [...forwardedRequestHeaders, ...forwardedResponseHeaders]) assert.equal(header, header.toLowerCase())
})
