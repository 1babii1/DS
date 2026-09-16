import { auth } from "@/auth";
import { AuthenticationRequiredError, getAccessToken } from "@/shared/auth/access-token";
import { authConfiguration } from "@/shared/auth/config";

export const dynamic = "force-dynamic";

const allowedPrefixes = ["/api/departments", "/api/positions", "/api/locations", "/api/employees", "/api/audit"];
const forwardedRequestHeaders = ["accept", "content-type", "if-match"];
const forwardedResponseHeaders = ["content-type", "location", "etag"];

function isAllowedPath(path: string): boolean {
  return allowedPrefixes.some((prefix) => path === prefix || path.startsWith(`${prefix}/`));
}

function hasExpectedOrigin(request: Request): boolean {
  const origin = request.headers.get("origin");
  return !origin || origin === authConfiguration.applicationUrl.origin;
}

async function proxy(request: Request, context: { params: Promise<{ path: string[] }> }): Promise<Response> {
  const session = await auth();
  if (!session?.user?.id) return Response.json({ detail: "Sign in is required." }, { status: 401 });
  if (!["GET", "HEAD"].includes(request.method) && !hasExpectedOrigin(request)) {
    return Response.json({ detail: "Invalid request origin." }, { status: 403 });
  }

  const { path } = await context.params;
  const upstreamPath = `/${path.join("/")}`;
  if (!isAllowedPath(upstreamPath)) return Response.json({ detail: "Route is not available." }, { status: 404 });

  try {
    const accessToken = await getAccessToken(session.user.id);
    const requestHeaders = new Headers();
    for (const header of forwardedRequestHeaders) {
      const value = request.headers.get(header);
      if (value) requestHeaders.set(header, value);
    }
    requestHeaders.set("authorization", `Bearer ${accessToken}`);

    const response = await fetch(new URL(`${upstreamPath}${new URL(request.url).search}`, authConfiguration.backendApiOrigin), {
      method: request.method,
      headers: requestHeaders,
      body: ["GET", "HEAD"].includes(request.method) ? undefined : await request.arrayBuffer(),
      cache: "no-store",
    });
    const responseHeaders = new Headers();
    for (const header of forwardedResponseHeaders) {
      const value = response.headers.get(header);
      if (value) responseHeaders.set(header, value);
    }
    return new Response(response.body, { status: response.status, headers: responseHeaders });
  } catch (error) {
    if (error instanceof AuthenticationRequiredError) {
      return Response.json({ detail: "Your session has expired. Sign in again." }, { status: 401 });
    }
    return Response.json({ detail: "The service is temporarily unavailable." }, { status: 503 });
  }
}

export const GET = proxy;
export const HEAD = proxy;
export const POST = proxy;
export const PUT = proxy;
export const PATCH = proxy;
export const DELETE = proxy;
