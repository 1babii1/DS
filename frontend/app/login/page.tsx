import { auth, signIn } from "@/auth";
import Link from "next/link";
import { redirect } from "next/navigation";

export const dynamic = "force-dynamic";

export default async function LoginPage({
  searchParams,
}: {
  searchParams: Promise<{ registered?: string }>;
}): Promise<React.ReactElement> {
  const session = await auth();
  if (session) redirect("/");
  const { registered } = await searchParams;

  return (
    <main className="grid min-h-screen place-items-center bg-background px-6">
      <section className="w-full max-w-md rounded-2xl border bg-card p-8 shadow-sm">
        <p className="text-sm font-semibold tracking-[0.18em] text-primary">DS / PEOPLE &amp; ORGANIZATION</p>
        <h1 className="mt-6 text-3xl font-semibold tracking-tight">Welcome back.</h1>
        <p className="mt-3 text-muted-foreground">Sign in securely to continue to your organization&apos;s workspace.</p>
        {registered === "1" ? <p className="mt-4 rounded-lg border border-primary/30 bg-primary/10 px-3 py-2 text-sm text-primary" role="status">Your account is ready. Continue to sign in.</p> : null}
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
        <p className="mt-6 text-sm text-muted-foreground">New here? <Link className="font-medium text-primary underline-offset-4 hover:underline" href="/register">Create a viewer account</Link></p>
      </section>
    </main>
  );
}
