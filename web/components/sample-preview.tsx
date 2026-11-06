"use client";

import { Badge } from "@/components/ui/badge";
import { Card } from "@/components/ui/card";
import { useI18n } from "@/lib/i18n";
import { Eye, Info } from "lucide-react";

/** A fixed illustration. It never comes from an analysis and no model produced it. */
export function SamplePreview() {
  const { t } = useI18n();

  return (
    <Card className="p-6" aria-labelledby="sample-title">
      <div className="flex flex-wrap items-center gap-2">
        <Badge tone="uncertain">
          <Info aria-hidden="true" className="size-3.5" />
          {t("sampleKicker")}
        </Badge>
      </div>
      <h2 id="sample-title" className="mt-3 text-xl font-semibold">
        {t("sampleTitle")}
      </h2>
      <p className="mt-2 text-sm leading-6 text-muted">{t("sampleBody")}</p>

      <figure className="mt-4 rounded-xl border border-white/12 bg-black/25 p-4">
        <figcaption className="mb-3 flex items-center justify-between gap-2 text-xs text-muted">
          <span>vakansiya-telebleri.pdf</span>
          <span>
            {t("page")} 3
          </span>
        </figcaption>
        <blockquote className="font-serif text-base leading-7 text-foreground">
          <mark className="quote-mark">{t("sampleQuote")}</mark>
        </blockquote>
      </figure>

      <div className="mt-4 border-l-2 border-accent pl-4">
        <p className="text-sm font-medium">{t("sampleRequirement")}</p>
        <p className="mt-2 text-sm text-muted">{t("sampleEvidence")}</p>
        <Badge className="mt-3">
          <Eye aria-hidden="true" className="size-3.5" />
          {t("sampleStatus")}
        </Badge>
      </div>
    </Card>
  );
}
