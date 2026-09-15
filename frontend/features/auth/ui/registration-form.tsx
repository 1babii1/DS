"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useTransition } from "react";
import { useForm } from "react-hook-form";
import { z } from "zod";

import { registerViewer, type RegistrationValues } from "@/app/register/actions";

const registrationSchema = z
  .object({
    email: z.string().trim().email("Enter a valid work email."),
    password: z.string().min(8, "Use at least 8 characters."),
    confirmPassword: z.string(),
  })
  .refine((values) => values.password === values.confirmPassword, {
    message: "Passwords do not match.",
    path: ["confirmPassword"],
  });

type FormValues = z.infer<typeof registrationSchema>;

function FieldError({ message, name }: { message?: string; name: string }) {
  return message ? <p className="mt-2 text-sm text-red-300" id={`${name}-error`} role="alert">{message}</p> : null;
}

export function RegistrationForm() {
  const router = useRouter();
  const [isPending, startTransition] = useTransition();
  const form = useForm<FormValues>({
    defaultValues: { confirmPassword: "", email: "", password: "" },
    resolver: zodResolver(registrationSchema),
  });

  const submit = (values: FormValues) => {
    const registration: RegistrationValues = { email: values.email, password: values.password };
    startTransition(async () => {
      const result = await registerViewer(registration);
      if (result.success) {
        router.push("/login?registered=1");
        return;
      }
      form.setError("root", { message: result.error });
    });
  };

  return (
    <main className="grid min-h-screen place-items-center bg-background px-6">
      <section className="w-full max-w-md rounded-2xl border bg-card p-8 shadow-sm">
        <p className="text-sm font-semibold tracking-[0.18em] text-primary">DS / PEOPLE &amp; ORGANIZATION</p>
        <h1 className="mt-6 text-3xl font-semibold tracking-tight">Create your workspace account.</h1>
        <p className="mt-3 text-muted-foreground">Start with viewer access. An administrator can grant additional permissions later.</p>
        <form className="mt-8 space-y-5" onSubmit={form.handleSubmit(submit)}>
          <label className="block text-sm font-medium" htmlFor="registration-email">Work email</label>
          <input aria-describedby={form.formState.errors.email ? "registration-email-error" : undefined} aria-invalid={Boolean(form.formState.errors.email)} autoComplete="email" className="mt-2 w-full rounded-lg border bg-background px-3 py-2.5 outline-none focus-visible:ring-2 focus-visible:ring-ring" id="registration-email" type="email" {...form.register("email")} />
          <FieldError message={form.formState.errors.email?.message} name="registration-email" />
          <label className="block text-sm font-medium" htmlFor="registration-password">Password</label>
          <input aria-describedby={form.formState.errors.password ? "registration-password-error" : undefined} aria-invalid={Boolean(form.formState.errors.password)} autoComplete="new-password" className="mt-2 w-full rounded-lg border bg-background px-3 py-2.5 outline-none focus-visible:ring-2 focus-visible:ring-ring" id="registration-password" type="password" {...form.register("password")} />
          <FieldError message={form.formState.errors.password?.message} name="registration-password" />
          <label className="block text-sm font-medium" htmlFor="registration-confirm-password">Confirm password</label>
          <input aria-describedby={form.formState.errors.confirmPassword ? "registration-confirm-password-error" : undefined} aria-invalid={Boolean(form.formState.errors.confirmPassword)} autoComplete="new-password" className="mt-2 w-full rounded-lg border bg-background px-3 py-2.5 outline-none focus-visible:ring-2 focus-visible:ring-ring" id="registration-confirm-password" type="password" {...form.register("confirmPassword")} />
          <FieldError message={form.formState.errors.confirmPassword?.message} name="registration-confirm-password" />
          {form.formState.errors.root ? <p className="text-sm text-red-300" role="alert">{form.formState.errors.root.message}</p> : null}
          <button className="w-full rounded-lg bg-primary px-4 py-3 font-medium text-primary-foreground transition-colors hover:bg-primary/90 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-ring disabled:cursor-not-allowed disabled:opacity-60" disabled={isPending} type="submit">{isPending ? "Creating account…" : "Create viewer account"}</button>
        </form>
        <p className="mt-6 text-sm text-muted-foreground">Already have an account? <Link className="font-medium text-primary underline-offset-4 hover:underline" href="/login">Sign in</Link></p>
      </section>
    </main>
  );
}
