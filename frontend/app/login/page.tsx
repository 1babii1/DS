import { auth, signIn } from "@/auth";
import { redirect } from "next/navigation";

export const dynamic = "force-dynamic";

export default async function LoginPage(): Promise<React.ReactElement> {
  const session = await auth();
  if (session) redirect("/");

  return (
    <main className="grid min-h-screen place-items-center bg-background px-6">
      <section className="w-full max-w-md rounded-2xl border bg-card p-8 shadow-sm">
        <p className="text-sm font-semibold tracking-[0.18em] text-primary">DS / PEOPLE &amp; ORGANIZATION</p>
        <h1 className="mt-6 text-3xl font-semibold tracking-tight">Welcome back.</h1>
        <p className="mt-3 text-muted-foreground">Sign in securely to continue to your organization&apos;s workspace.</p>
        <form
          className="mt-8"
          action={async () => {
            "use server";
            await signIn("openiddict", { redirectTo: "/" });
          }}
        >
          <button className="w-full rounded-lg bg-primary px-4 py-3 font-medium text-primary-foreground transition-colors hover:bg-primary/90 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-ring">
            Continue to sign in
          </button>
        </form>
      </section>
    </main>
  );
}
