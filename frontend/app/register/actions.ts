"use server";

import { z } from "zod";

import { authConfiguration } from "@/shared/auth/config";

const registrationSchema = z.object({
  email: z.string().trim().email("Enter a valid work email."),
  password: z
    .string()
    .min(8, "Use at least 8 characters.")
    .regex(/[A-Z]/, "Include an uppercase letter.")
    .regex(/[a-z]/, "Include a lowercase letter.")
    .regex(/[0-9]/, "Include a number."),
});

export type RegistrationValues = z.infer<typeof registrationSchema>;
type RegistrationResult = { error?: string; success: boolean };

export async function registerViewer(values: RegistrationValues): Promise<RegistrationResult> {
  const parsed = registrationSchema.safeParse(values);
  if (!parsed.success) return { error: "Check your email and password, then try again.", success: false };

  try {
    const response = await fetch(new URL("auth/register", authConfiguration.issuer), {
      body: JSON.stringify(parsed.data),
      cache: "no-store",
      headers: { "content-type": "application/json" },
      method: "POST",
    });

    if (response.ok) return { success: true };
    if (response.status === 429) return { error: "Too many registration attempts. Please wait and try again.", success: false };
    return { error: "We couldn't create that account. Try a different email or password.", success: false };
  } catch {
    return { error: "Registration is temporarily unavailable. Please try again shortly.", success: false };
  }
}
