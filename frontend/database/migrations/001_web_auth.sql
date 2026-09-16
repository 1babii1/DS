CREATE SCHEMA IF NOT EXISTS web_auth;
CREATE EXTENSION IF NOT EXISTS pgcrypto;

CREATE TABLE IF NOT EXISTS web_auth.users (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    name text,
    email text UNIQUE,
    "emailVerified" timestamptz,
    image text
);

CREATE TABLE IF NOT EXISTS web_auth.accounts (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    "userId" uuid NOT NULL REFERENCES web_auth.users(id) ON DELETE CASCADE,
    type text NOT NULL,
    provider text NOT NULL,
    "providerAccountId" text NOT NULL,
    refresh_token text,
    access_token text,
    expires_at bigint,
    token_type text,
    scope text,
    id_token text,
    session_state text,
    UNIQUE (provider, "providerAccountId")
);

CREATE TABLE IF NOT EXISTS web_auth.sessions (
    id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    "sessionToken" text NOT NULL UNIQUE,
    "userId" uuid NOT NULL REFERENCES web_auth.users(id) ON DELETE CASCADE,
    expires timestamptz NOT NULL
);

CREATE TABLE IF NOT EXISTS web_auth.verification_token (
    identifier text NOT NULL,
    token text NOT NULL,
    expires timestamptz NOT NULL,
    PRIMARY KEY (identifier, token)
);

CREATE INDEX IF NOT EXISTS accounts_user_id_index ON web_auth.accounts ("userId");
CREATE INDEX IF NOT EXISTS sessions_user_id_index ON web_auth.sessions ("userId");
