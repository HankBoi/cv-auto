"use client";

import { buttonVariants } from "@/components/ui/button";
import { useQuota } from "@/lib/hooks";
import { LOCALES, useI18n } from "@/lib/i18n";
import { cn } from "@/lib/utils";
import { FolderOpen, History, Home, Menu, PlusCircle, Sparkles, X } from "lucide-react";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { useState } from "react";

const NAV = [
  { href: "/", key: "nav.home", icon: Home },
  { href: "/new", key: "nav.new", icon: PlusCircle },
  { href: "/analyses", key: "nav.history", icon: History },
  { href: "/documents", key: "nav.documents", icon: FolderOpen },
  { href: "/plan", key: "nav.plan", icon: Sparkles },
];

function isActive(pathname: string, href: string) {
  return href === "/" ? pathname === "/" : pathname === href || pathname.startsWith(`${href}/`);
}

export function SiteHeader() {
  const { locale, setLocale, t } = useI18n();
  const pathname = usePathname();
  const [open, setOpen] = useState(false);
  const { quota } = useQuota(pathname);

  return (
    <header className="sticky top-0 z-40 border-b border-white/10 bg-[#071018]/70 backdrop-blur-xl">
      <div className="mx-auto flex max-w-6xl items-center justify-between gap-3 px-4 py-3">
        <Link href="/" className="flex min-w-0 items-center rounded-xl">
          {/* eslint-disable-next-line @next/next/no-img-element */}
          <img src="/statific-logo.png" alt="statific" className="h-11 w-auto object-contain" />
        </Link>

        <nav aria-label={t("nav.label")} className="hidden items-center gap-1 lg:flex">
          {NAV.map(({ href, key, icon: Icon }) => {
            const current = isActive(pathname, href);
            return (
              <Link
                key={href}
                href={href}
                aria-current={current ? "page" : undefined}
                className={cn(
                  "inline-flex min-h-10 items-center gap-1.5 rounded-xl px-3 text-sm transition-colors hover:bg-white/10",
                  current ? "bg-white/12 text-mint" : "text-foreground/90",
                )}
              >
                <Icon aria-hidden="true" className="size-4" />
                {t(key)}
              </Link>
            );
          })}
        </nav>

        <div className="flex items-center gap-2">
          {quota ? (
            <Link
              href="/plan"
              className="hidden min-h-9 items-center rounded-full border border-mint/30 bg-blue-400/10 px-3 text-xs text-mint sm:inline-flex"
              title={t("header.quotaTitle")}
            >
              {quota.subscription_active
                ? t("header.planActive")
                : t("header.freeLeft", { n: quota.free_remaining, total: quota.free_limit })}
            </Link>
          ) : null}
          <div
            role="group"
            aria-label={t("language")}
            className="glass flex items-center rounded-xl p-0.5"
          >
            {LOCALES.map((code) => (
              <button
                key={code}
                type="button"
                lang={code}
                aria-pressed={locale === code}
                onClick={() => setLocale(code)}
                className={cn(
                  "min-h-9 min-w-9 rounded-[0.65rem] px-2 text-xs font-semibold transition-colors focus-visible:outline-2 focus-visible:outline-mint",
                  locale === code ? "bg-accent text-accent-foreground" : "text-foreground hover:bg-white/10",
                )}
              >
                {code.toUpperCase()}
              </button>
            ))}
          </div>
          <button
            type="button"
            className={cn(buttonVariants({ variant: "outline", size: "sm" }), "lg:hidden")}
            aria-expanded={open}
            aria-controls="mobile-nav"
            aria-label={open ? t("nav.close") : t("nav.open")}
            onClick={() => setOpen((value) => !value)}
          >
            {open ? <X aria-hidden="true" className="size-4" /> : <Menu aria-hidden="true" className="size-4" />}
          </button>
        </div>
      </div>

      {open ? (
        <nav
          id="mobile-nav"
          aria-label={t("nav.label")}
          className="mx-auto grid max-w-6xl gap-1 border-t border-white/10 px-4 py-3 lg:hidden"
        >
          {NAV.map(({ href, key, icon: Icon }) => {
            const current = isActive(pathname, href);
            return (
              <Link
                key={href}
                href={href}
                aria-current={current ? "page" : undefined}
                onClick={() => setOpen(false)}
                className={cn(
                  "inline-flex min-h-11 items-center gap-2 rounded-xl px-3 text-sm hover:bg-white/10",
                  current ? "bg-white/12 text-mint" : "",
                )}
              >
                <Icon aria-hidden="true" className="size-4" />
                {t(key)}
              </Link>
            );
          })}
          {quota ? (
            <p className="px-3 pt-1 text-xs text-muted">
              {quota.subscription_active
                ? t("header.planActive")
                : t("header.freeLeft", { n: quota.free_remaining, total: quota.free_limit })}
            </p>
          ) : null}
        </nav>
      ) : null}
    </header>
  );
}
