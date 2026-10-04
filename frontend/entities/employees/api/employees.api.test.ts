import assert from 'node:assert/strict'
import { test } from 'node:test'
import { AxiosHeaders, type InternalAxiosRequestConfig } from 'axios'

import { axiosInstance } from '@/shared/api/axiosInstance'
import { employeesApi } from './employees.api.ts'

async function withRecordedRequest(run: (request: () => InternalAxiosRequestConfig | undefined) => Promise<void>) {
	const adapter = axiosInstance.defaults.adapter
	let request: InternalAxiosRequestConfig | undefined
	axiosInstance.defaults.adapter = async config => {
		request = config
		return { config, data: {}, headers: new AxiosHeaders(), status: 200, statusText: 'OK' }
	}
	try {
		await run(() => request)
	} finally {
		axiosInstance.defaults.adapter = adapter
	}
}

test('a card read carries the requested wallet version', async () => {
	await withRecordedRequest(async request => {
		await employeesApi.card('anna', 3)
		assert.equal(new AxiosHeaders(request()?.headers).get('X-Min-Wallet-Version'), '3')
	})
})

test('a normal card read sends no minimum wallet version', async () => {
	await withRecordedRequest(async request => {
		await employeesApi.card('anna')
		assert.equal(new AxiosHeaders(request()?.headers).has('X-Min-Wallet-Version'), false)
	})
})
