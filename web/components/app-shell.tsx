"use client";

import { DotField } from "@/components/ambient/dot-field";
import { SiteHeader } from "@/components/site-header";
import { useI18n } from "@/lib/i18n";
import type { ReactNode } from "react";

export function AppShell({ children }: { children: ReactNode }) {
  const { t } = useI18n();
  return (
    <>
      <DotField />
      <a href="#main" className="skip-link">
        {t("skip")}
      </a>
      <div className="relative z-10 flex min-h-screen flex-col">
        <SiteHeader />
        <main id="main" tabIndex={-1} className="mx-auto w-full max-w-6xl flex-1 px-4 py-8 outline-none sm:py-10">
          {children}
        </main>
        <footer className="border-t border-white/10 bg-[#071018]/60 px-4 py-6 text-xs leading-5 text-muted backdrop-blur-md">
          <div className="mx-auto grid max-w-6xl gap-1">
            <p>{t("footer.boundary")}</p>
            <p>{t("footer.official")}</p>
            <p>{t("footer.demo")}</p>
          </div>
        </footer>
      </div>
    </>
  );
}
