import axios from 'axios'
import { Envelope } from './envelop'
import { EnvelopeError } from './errors'

export const axiosInstance = axios.create({
	baseURL: 'http://localhost:5129/',
	headers: {
		'Content-Type': 'application/json'
	}
})

axiosInstance.interceptors.response.use(
	response => {
		if (response.data?.isError) {
			throw new EnvelopeError(response.data.errorList[0])
		}
		return response
	},
	error => {
		if (axios.isAxiosError(error) && error.response?.data) {
			const envelope = error.response.data as Envelope

			if (envelope?.isError && envelope.errorList) {
				throw new EnvelopeError(envelope.errorList[0])
			}
		}

		return Promise.reject(error)
	}
)
