"use client";

import { FloatingEmoji } from "@/components/ambient/floating-emoji";
import { Reveal } from "@/components/ambient/reveal";
import { SamplePreview } from "@/components/sample-preview";
import { buttonVariants } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { useI18n } from "@/lib/i18n";
import { cn } from "@/lib/utils";
import {
  CalendarClock,
  ClipboardCheck,
  FileCheck2,
  FileStack,
  FileText,
  FolderOpen,
  ListChecks,
  Scale,
  ShieldQuestion,
  Wrench,
  ArrowRight,
  type LucideIcon,
} from "lucide-react";
import Link from "next/link";

const STEPS: { icon: LucideIcon; key: string }[] = [
  { icon: FileText, key: "upload" },
  { icon: FileStack, key: "company" },
  { icon: ClipboardCheck, key: "review" },
  { icon: ListChecks, key: "export" },
];

const FINDS: { icon: LucideIcon; key: string }[] = [
  { icon: FileCheck2, key: "required_document" },
  { icon: ShieldQuestion, key: "missing" },
  { icon: CalendarClock, key: "deadline" },
  { icon: Wrench, key: "technical_mismatch" },
  { icon: Scale, key: "contract_terms" },
  { icon: ClipboardCheck, key: "conflicting_unclear" },
];

export function HomeScreen() {
  const { t } = useI18n();

  return (
    <div className="grid gap-16">
      <section className="relative isolate overflow-hidden rounded-3xl px-2 pt-20 pb-16 text-center sm:px-10 sm:pt-16 sm:pb-20">
        <FloatingEmoji />
        <div className="relative mx-auto grid max-w-3xl justify-items-center gap-5">
          <p className="glass rounded-full px-3.5 py-1 text-xs tracking-[0.14em] text-accent uppercase">
            {t("hero.kicker")}
          </p>
          <h1 className="text-4xl leading-[1.08] font-semibold tracking-tight sm:text-6xl">
            <span className="text-gradient">{t("hero.title")}</span>
          </h1>
          <p className="font-serif text-xl text-foreground sm:text-2xl">{t("tagline")}</p>
          <p className="max-w-2xl text-base leading-7 text-muted sm:text-lg">{t("description")}</p>
          <div className="mt-2 flex flex-wrap justify-center gap-3">
            <Link href="/new" className={cn(buttonVariants({ size: "lg" }))}>
              {t("hero.cta")}
              <ArrowRight aria-hidden="true" className="size-5" />
            </Link>
            <Link href="/documents" className={cn(buttonVariants({ size: "lg", variant: "outline" }))}>
              <FolderOpen aria-hidden="true" className="size-5" />
              {t("hero.secondary")}
            </Link>
          </div>
          <ul className="mt-3 flex flex-wrap justify-center gap-2 text-xs text-muted">
            <li className="glass rounded-full px-3 py-1">{t("hero.chipNoLogin")}</li>
            <li className="glass rounded-full px-3 py-1">{t("hero.chipSource")}</li>
            <li className="glass rounded-full px-3 py-1">{t("hero.chipHuman")}</li>
          </ul>
        </div>
      </section>

      <section aria-labelledby="steps-heading" className="grid gap-6">
        <Reveal>
          <h2 id="steps-heading" className="text-2xl font-semibold sm:text-3xl">
            {t("steps.title")}
          </h2>
        </Reveal>
        <ol className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
          {STEPS.map(({ icon: Icon, key }, index) => (
            <li key={key}>
              <Reveal delay={index * 90} className="h-full">
                <Card className="tilt-card h-full p-5">
                  <div className="flex items-center gap-3">
                    <span className="grid size-10 place-items-center rounded-xl bg-blue-400/15 text-mint">
                      <Icon aria-hidden="true" className="size-5" />
                    </span>
                    <span className="text-sm text-muted">{index + 1}</span>
                  </div>
                  <h3 className="mt-4 text-lg font-semibold">{t(`steps.${key}.title`)}</h3>
                  <p className="mt-2 text-sm leading-6 text-muted">{t(`steps.${key}.body`)}</p>
                </Card>
              </Reveal>
            </li>
          ))}
        </ol>
      </section>

      <section aria-labelledby="finds-heading" className="grid gap-6">
        <Reveal>
          <h2 id="finds-heading" className="text-2xl font-semibold sm:text-3xl">
            {t("finds.title")}
          </h2>
          <p className="mt-2 max-w-2xl text-muted">{t("finds.subtitle")}</p>
        </Reveal>
        <ul className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
          {FINDS.map(({ icon: Icon, key }, index) => (
            <li key={key}>
              <Reveal delay={index * 70} className="h-full">
                <Card className="tilt-card h-full p-5">
                  <Icon aria-hidden="true" className="size-6 text-mint" />
                  <h3 className="mt-3 font-semibold">{key === "missing" ? t("finds.missingTitle") : t(`category.${key}`)}</h3>
                  <p className="mt-1.5 text-sm leading-6 text-muted">{t(`finds.${key}`)}</p>
                </Card>
              </Reveal>
            </li>
          ))}
        </ul>
      </section>

      <div className="grid items-start gap-6 lg:grid-cols-2">
        <Reveal>
          <SamplePreview />
        </Reveal>
        <Reveal delay={120}>
          <Card className="p-6" aria-labelledby="boundary-heading">
            <h2 id="boundary-heading" className="text-xl font-semibold">
              {t("boundaryTitle")}
            </h2>
            <p className="mt-3 leading-7 text-foreground/90">{t("boundaryBody")}</p>
            <ul className="mt-4 grid gap-2 text-sm leading-6 text-muted">
              <li>• {t("boundary.l1")}</li>
              <li>• {t("boundary.l2")}</li>
              <li>• {t("boundary.l3")}</li>
              <li>• {t("boundary.l4")}</li>
            </ul>
            <p className="mt-4 text-sm text-muted">{t("notOfficial")}</p>
          </Card>
        </Reveal>
      </div>
    </div>
  );
}
