"use client";

import { Eye, EyeOff } from "lucide-react";
import { forwardRef, useState, type ComponentProps } from "react";

import { cn } from "@/shared/lib/utils";

type PasswordInputProps = Omit<ComponentProps<"input">, "type"> & {
  hideLabel?: string;
  showLabel?: string;
};

export const PasswordInput = forwardRef<HTMLInputElement, PasswordInputProps>(function PasswordInput(
  { className, hideLabel = "Hide password", showLabel = "Show password", ...props },
  ref,
) {
  const [visible, setVisible] = useState(false);

  return <div className="relative">
    <input ref={ref} {...props} className={cn("w-full rounded-lg border bg-background px-3 py-2.5 pr-12 outline-none focus-visible:ring-2 focus-visible:ring-ring", className)} type={visible ? "text" : "password"} />
    <button aria-label={visible ? hideLabel : showLabel} aria-pressed={visible} className="absolute inset-y-0 right-0 inline-flex w-11 items-center justify-center text-muted-foreground hover:text-foreground focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-ring" onClick={() => setVisible((current) => !current)} type="button">
      {visible ? <EyeOff aria-hidden="true" size={18} /> : <Eye aria-hidden="true" size={18} />}
    </button>
  </div>;
});
