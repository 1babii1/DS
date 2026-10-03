import assert from 'node:assert/strict'
import { beforeEach, test } from 'node:test'

import { forgetWalletVersions, rememberWalletVersion, walletVersionFor } from './wallet-version.ts'

beforeEach(forgetWalletVersions)

test('the version a grant answered with is what the next read of that card asks for', () => {
	rememberWalletVersion('anna', '3')
	assert.equal(walletVersionFor('anna'), 3)
})

test('versions are kept per employee, never reused for someone else', () => {
	rememberWalletVersion('anna', '3')
	assert.equal(walletVersionFor('boris'), undefined)
})

test('an older answer arriving late does not lower the version', () => {
	rememberWalletVersion('anna', '5')
	rememberWalletVersion('anna', '4')
	assert.equal(walletVersionFor('anna'), 5)
})

test('a missing or unreadable header is ignored', () => {
	for (const header of [undefined, null, '', 'abc', '0', '-2', '1.5x']) rememberWalletVersion('anna', header)
	assert.equal(walletVersionFor('anna'), undefined)
})
