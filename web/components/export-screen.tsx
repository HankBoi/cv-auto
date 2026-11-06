"use client";

import { Alert } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Label } from "@/components/ui/label";
import { downloadChecklist, getAnalysis, type Analysis } from "@/lib/api";
import { useErrorText } from "@/lib/hooks";
import { LOCALES, useI18n, type Locale } from "@/lib/i18n";
import { ArrowLeft, Download, Loader2 } from "lucide-react";
import Link from "next/link";
import { useEffect, useState } from "react";

export function ExportScreen({ id }: { id: string }) {
  const { t, locale } = useI18n();
  const errorText = useErrorText();
  const [analysis, setAnalysis] = useState<Analysis | null>(null);
  const [language, setLanguage] = useState<Locale | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    let stop = false;
    getAnalysis(id)
      .then((value) => {
        if (!stop) setAnalysis(value);
      })
      .catch((caught) => {
        if (!stop) setError(errorText(caught));
      });
    return () => {
      stop = true;
    };
  }, [id, errorText]);

  const chosen = language ?? locale;
  const summary = analysis?.summary;
  const open = summary ? summary.findings_total - summary.reviewed : 0;

  async function download() {
    setBusy(true);
    setError(null);
    setNotice(null);
    try {
      await downloadChecklist(id, chosen);
      setNotice(t("exportPage.done"));
    } catch (caught) {
      setError(errorText(caught));
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="grid max-w-3xl gap-6">
      <div>
        <Link href={`/analyses/${id}`} className="inline-flex items-center gap-1.5 text-sm text-mint underline-offset-4 hover:underline">
          <ArrowLeft aria-hidden="true" className="size-4" />
          {t("exportPage.back")}
        </Link>
        <h1 className="mt-2 text-3xl font-semibold tracking-tight sm:text-4xl">{t("exportPage.title")}</h1>
        <p className="mt-2 leading-7 text-muted">{analysis?.label ?? t("loading")}</p>
      </div>

      {error ? (
        <Alert tone="danger" role="alert">
          {error}
        </Alert>
      ) : null}
      {notice ? <Alert tone="success">{notice}</Alert> : null}

      {analysis && summary ? (
        <>
          {summary.findings_total === 0 ? <Alert tone="warn">{t("exportPage.empty")}</Alert> : null}
          {open > 0 && summary.findings_total > 0 ? (
            <Alert tone="warn">{t("exportPage.openItems", { n: open, total: summary.findings_total })}</Alert>
          ) : null}
          {summary.source_not_verified > 0 ? (
            <Alert tone="warn">{t("exportPage.unverified", { n: summary.source_not_verified })}</Alert>
          ) : null}
          {analysis.is_sample ? <Alert>{t("sample.banner")}</Alert> : null}

          <Card className="grid gap-4 p-6" aria-labelledby="export-heading">
            <h2 id="export-heading" className="text-lg font-semibold">
              {t("exportPage.contentsTitle")}
            </h2>
            <ul className="grid gap-1.5 text-sm leading-6 text-muted">
              <li>• {t("exportPage.c1")}</li>
              <li>• {t("exportPage.c2")}</li>
              <li>• {t("exportPage.c3")}</li>
              <li>• {t("exportPage.c4")}</li>
            </ul>
            <div className="grid gap-1.5">
              <Label htmlFor="export-language">{t("exportPage.language")}</Label>
              <select
                id="export-language"
                value={chosen}
                onChange={(event) => setLanguage(event.target.value as Locale)}
                className="min-h-10 w-full max-w-xs rounded-xl border border-white/15 bg-[#0c1c33] px-3 text-sm text-foreground focus-visible:outline-2 focus-visible:outline-mint"
              >
                {LOCALES.map((code) => (
                  <option key={code} value={code}>
                    {t(`languageName.${code}`)}
                  </option>
                ))}
              </select>
            </div>
            <div>
              <Button type="button" size="lg" disabled={busy || summary.findings_total === 0} onClick={() => void download()}>
                {busy ? <Loader2 aria-hidden="true" className="size-5 animate-spin" /> : <Download aria-hidden="true" className="size-5" />}
                {t("exportPage.download")}
              </Button>
            </div>
            <p className="text-xs leading-5 text-muted">{t("exportPage.limits")}</p>
          </Card>
        </>
      ) : !error ? (
        <Alert>{t("loading")}</Alert>
      ) : null}
    </div>
  );
}
