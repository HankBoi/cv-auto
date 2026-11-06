import { cva, type VariantProps } from "class-variance-authority";
import type * as React from "react";
import { cn } from "@/lib/utils";

const badgeVariants = cva(
  "inline-flex items-center gap-1 rounded-full border px-2.5 py-0.5 text-xs font-medium",
  {
    variants: {
      tone: {
        neutral: "border-white/15 bg-white/8 text-foreground",
        mint: "border-mint/40 bg-blue-400/12 text-mint",
        high: "border-red-300/50 bg-red-500/15 text-red-100",
        medium: "border-amber-300/50 bg-amber-400/15 text-amber-100",
        low: "border-sky-300/45 bg-sky-400/12 text-sky-100",
        uncertain: "border-slate-300/40 bg-slate-400/12 text-slate-100",
      },
    },
    defaultVariants: { tone: "neutral" },
  },
);

export function Badge({
  className,
  tone,
  ...props
}: React.ComponentProps<"span"> & VariantProps<typeof badgeVariants>) {
  return <span className={cn(badgeVariants({ tone }), className)} {...props} />;
}
