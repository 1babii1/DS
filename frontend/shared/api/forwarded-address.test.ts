import { appendForwardedFor, forwardedForFromIncomingHeaders } from './forwarded-address.ts'
import assert from 'node:assert/strict'
import { test } from 'node:test'

test('appends the browser address chain when the BFF proxies a request', () => {
	assert.equal(
		appendForwardedFor('198.51.100.7', '203.0.113.9'),
		'198.51.100.7, 203.0.113.9'
	)
	assert.equal(appendForwardedFor(null, '203.0.113.9'), '203.0.113.9')
	assert.equal(
		appendForwardedFor(null, '198.51.100.7, 203.0.113.9'),
		'198.51.100.7, 203.0.113.9'
	)
})

test('does not forward malformed client-address values', () => {
	assert.equal(
		appendForwardedFor('198.51.100.7\r\nX-Injected: yes', '203.0.113.9'),
		'203.0.113.9'
	)
	assert.equal(appendForwardedFor(null, 'not-an-address'), null)
})

test('the BFF uses the incoming forwarded chain before its single-address fallback', () => {
	assert.equal(forwardedForFromIncomingHeaders(new Headers({ 'x-forwarded-for': '198.51.100.7, 203.0.113.9', 'x-real-ip': '192.0.2.5' })), '198.51.100.7, 203.0.113.9')
	assert.equal(forwardedForFromIncomingHeaders(new Headers({ 'x-real-ip': '203.0.113.9' })), '203.0.113.9')
})
