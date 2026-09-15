import http from "k6/http";
import crypto from "k6/crypto";

const AUTH_BASE_URL = __ENV.AUTH_BASE_URL || "http://localhost:5130";
const ADMIN_EMAIL = __ENV.ADMIN_EMAIL || "admin@portfolio.local";
const ADMIN_PASSWORD = __ENV.ADMIN_PASSWORD || "ChangeMe123!";
const CLIENT_ID = "portfolio-frontend";
const REDIRECT_URI = "http://localhost:3000/auth/callback";
// PKCE code_verifier must be 43-128 chars of unreserved characters (RFC 7636).
const CODE_VERIFIER = "k6-load-test-code-verifier-string-43-chars-min-xxxx";

// Runs the full authorization_code + PKCE flow the same way a real browser
// client would, so this measures the whole login path, not just a token
// endpoint shortcut.
export function getAdminToken() {
  const jar = http.cookieJar();

  const loginRes = http.post(
    `${AUTH_BASE_URL}/auth/login`,
    JSON.stringify({ email: ADMIN_EMAIL, password: ADMIN_PASSWORD }),
    { headers: { "Content-Type": "application/json" }, jar },
  );
  if (loginRes.status !== 204) {
    throw new Error(`login failed: ${loginRes.status} ${loginRes.body}`);
  }

  const challenge = crypto.sha256(CODE_VERIFIER, "base64rawurl");

  const authorizeRes = http.get(
    `${AUTH_BASE_URL}/connect/authorize?client_id=${CLIENT_ID}&response_type=code` +
      `&redirect_uri=${encodeURIComponent(REDIRECT_URI)}&scope=openid%20profile%20email%20roles` +
      `&code_challenge=${challenge}&code_challenge_method=S256&state=xyz`,
    { jar, redirects: 0 },
  );
  const location = authorizeRes.headers.Location;
  if (!location) {
    throw new Error(`authorize did not redirect: ${authorizeRes.status} ${authorizeRes.body}`);
  }
  // k6's JS runtime has no global URL/URLSearchParams, so pull the code out by hand.
  const codeMatch = location.match(/[?&]code=([^&]+)/);
  const code = codeMatch && decodeURIComponent(codeMatch[1]);
  if (!code) {
    throw new Error(`no authorization code in redirect: ${location}`);
  }

  const tokenRes = http.post(`${AUTH_BASE_URL}/connect/token`, {
    grant_type: "authorization_code",
    code,
    redirect_uri: REDIRECT_URI,
    client_id: CLIENT_ID,
    code_verifier: CODE_VERIFIER,
  });
  if (tokenRes.status !== 200) {
    throw new Error(`token exchange failed: ${tokenRes.status} ${tokenRes.body}`);
  }

  return tokenRes.json("access_token");
}
