import NextAuth from "next-auth";
import PostgresAdapter from "@auth/pg-adapter";
import { authDatabase } from "@/shared/auth/database";
import { authConfiguration } from "@/shared/auth/config";
import { getCanEdit } from "@/shared/auth/access-token";

declare module "next-auth" {
  interface Session {
    user: {
      id: string;
      name?: string | null;
      email?: string | null;
      image?: string | null;
      canEdit: boolean;
    };
  }
}

export const { handlers, auth, signIn, signOut } = NextAuth({
  adapter: PostgresAdapter(authDatabase),
  session: {
    strategy: "database",
    maxAge: 60 * 60 * 8,
    updateAge: 60 * 60,
  },
  providers: [
    {
      id: "openiddict",
      name: "DS",
      type: "oidc",
      issuer: authConfiguration.issuer.toString(),
      clientId: authConfiguration.clientId,
      clientSecret: authConfiguration.clientSecret,
      authorization: {
        params: {
          scope: "openid profile email roles offline_access",
        },
      },
      checks: ["pkce", "state"],
      profile(profile) {
        return {
          id: profile.sub,
          name: profile.name,
          email: profile.email,
          image: null,
        };
      },
    },
  ],
  callbacks: {
    async session({ session, user }) {
      session.user.id = user.id;
      session.user.canEdit = await getCanEdit(user.id);
      return session;
    },
  },
});
