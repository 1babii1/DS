import axios from 'axios'

export const axiosInstance = axios.create({
	baseURL: '/api/backend',
	headers: { 'Content-Type': 'application/json' }
})
