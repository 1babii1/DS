// Which headers the BFF passes between the browser and the backend (app/api/backend/[...path]/route.ts). Kept apart from the route so that
// a test can name them: a header missing here is dropped silently, and the feature that depends on it just does not work.
export const forwardedRequestHeaders = ['accept', 'content-type', 'if-match', 'idempotency-key', 'x-min-wallet-version']
export const forwardedResponseHeaders = ['content-type', 'location', 'etag', 'retry-after', 'x-wallet-version', 'x-card-consistent']
