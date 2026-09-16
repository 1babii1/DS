import "server-only";

function getRequiredEnvironmentVariable(name: string): string {
  const value = process.env[name];
  if (!value) {
    throw new Error(`${name} is required to configure server authentication.`);
  }
  return value;
}

export const authConfiguration = {
  issuer: new URL(getRequiredEnvironmentVariable("AUTH_OIDC_ISSUER")),
  backendApiOrigin: new URL(getRequiredEnvironmentVariable("BACKEND_API_ORIGIN")),
  clientId: getRequiredEnvironmentVariable("AUTH_OIDC_CLIENT_ID"),
  clientSecret: getRequiredEnvironmentVariable("AUTH_OIDC_CLIENT_SECRET"),
  applicationUrl: new URL(getRequiredEnvironmentVariable("AUTH_URL")),
  databaseUrl: getRequiredEnvironmentVariable("DATABASE_URL"),
};

if (authConfiguration.applicationUrl.protocol !== "https:" && authConfiguration.applicationUrl.hostname !== "localhost") {
  throw new Error("AUTH_URL must use HTTPS outside local development.");
}

if (authConfiguration.backendApiOrigin.protocol !== "https:" && authConfiguration.backendApiOrigin.hostname !== "localhost") {
  throw new Error("BACKEND_API_ORIGIN must use HTTPS outside local development.");
}
