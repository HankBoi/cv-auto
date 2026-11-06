import { cva, type VariantProps } from "class-variance-authority";
import type * as React from "react";
import { cn } from "@/lib/utils";

const alertVariants = cva("rounded-xl border px-4 py-3 text-sm leading-6", {
  variants: {
    tone: {
      info: "glass border-white/15",
      success: "border-blue-300/40 bg-blue-400/10 text-blue-50",
      warn: "border-amber-300/45 bg-amber-400/10 text-amber-50",
      danger: "border-red-300/50 bg-red-500/12 text-red-50",
    },
  },
  defaultVariants: { tone: "info" },
});

export function Alert({
  className,
  tone,
  ...props
}: React.ComponentProps<"div"> & VariantProps<typeof alertVariants>) {
  return <div role="status" className={cn(alertVariants({ tone }), className)} {...props} />;
}
