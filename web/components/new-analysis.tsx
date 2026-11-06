"use client";

import { FilePicker } from "@/components/file-picker";
import { Alert } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import {
  ApiError,
  createAnalysis,
  DEMO_EVIDENCE,
  DEMO_TENDER,
  filesFromDemo,
  listDocuments,
  type LibraryDocument,
} from "@/lib/api";
import { formatBytes, useConfig, useErrorText, useQuota } from "@/lib/hooks";
import { LOCALES, useI18n, type Locale } from "@/lib/i18n";
import { FileText, FlaskConical, Loader2, Paperclip, X } from "lucide-react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useEffect, useState } from "react";

export function NewAnalysis() {
  const { t, locale } = useI18n();
  const router = useRouter();
  const errorText = useErrorText();
  const { quota, reload: reloadQuota } = useQuota();
  const { config, failed: configFailed } = useConfig();

  const [label, setLabel] = useState("");
  const [tender, setTender] = useState<File | null>(null);
  const [extra, setExtra] = useState<File[]>([]);
  const [library, setLibrary] = useState<LibraryDocument[] | null>(null);
  const [selected, setSelected] = useState<string[]>([]);
  const [language, setLanguage] = useState<Locale | null>(null);
  const [busy, setBusy] = useState<"live" | "sample" | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [planBlocked, setPlanBlocked] = useState(false);

  const findingLanguage = language ?? locale;
  const limit = config?.limits.max_company_documents_per_analysis ?? 8;
  const companyCount = selected.length + extra.length;
  const modelMissing = config !== null && !config.model_configured;
  const blockedByPlan = planBlocked || (quota !== null && !quota.can_start);

  useEffect(() => {
    let stop = false;
    listDocuments()
      .then((body) => {
        if (!stop) setLibrary(body.documents);
      })
      .catch(() => {
        if (!stop) setLibrary([]);
      });
    return () => {
      stop = true;
    };
  }, []);

  async function submitLive() {
    setError(null);
    if (label.trim().length < 2) {
      setError(t("new.errName"));
      return;
    }
    if (!tender) {
      setError(t("new.errTender"));
      return;
    }
    if (companyCount > limit) {
      setError(t("errors.too_many_files"));
      return;
    }
    setBusy("live");
    try {
      const created = await createAnalysis(label.trim(), tender, {
        language: findingLanguage,
        documentIds: selected,
        files: extra,
      });
      router.push(`/analyses/${created.analysis_id}`);
    } catch (caught) {
      await handleFailure(caught);
    }
  }

  async function submitSample() {
    setError(null);
    setBusy("sample");
    try {
      const [sampleTender] = await filesFromDemo([DEMO_TENDER]);
      const evidence = await filesFromDemo(DEMO_EVIDENCE.map((name) => `/demo/${name}`));
      const created = await createAnalysis(label.trim().length >= 2 ? label.trim() : t("new.sampleLabel"), sampleTender, {
        language: findingLanguage,
        sample: true,
        files: evidence,
      });
      router.push(`/analyses/${created.analysis_id}`);
    } catch (caught) {
      await handleFailure(caught);
    }
  }

  async function handleFailure(caught: unknown) {
    if (caught instanceof ApiError && caught.code === "plan_required") {
      setPlanBlocked(true);
      void reloadQuota();
    }
    setError(errorText(caught));
    setBusy(null);
  }

  function toggle(id: string) {
    setSelected((current) => (current.includes(id) ? current.filter((item) => item !== id) : [...current, id]));
  }

  const disabled = busy !== null || modelMissing;

  return (
    <div className="grid gap-8">
      <div>
        <h1 className="text-3xl font-semibold tracking-tight sm:text-4xl">{t("new.title")}</h1>
        <p className="mt-2 max-w-2xl leading-7 text-muted">{t("new.subtitle")}</p>
      </div>

      {modelMissing ? (
        <Alert tone="warn" role="alert">
          <p className="font-medium">{t("errors.model_not_configured")}</p>
          <p className="mt-1 text-amber-100/80">{t("new.setupHint")}</p>
        </Alert>
      ) : null}
      {configFailed ? (
        <Alert tone="danger" role="alert">
          {t("errors.api_unreachable")}
        </Alert>
      ) : null}
      {blockedByPlan ? (
        <Alert tone="warn" role="alert">
          <p className="font-medium">{t("errors.plan_required")}</p>
          <Link href="/plan" className="mt-2 inline-block font-medium text-mint underline underline-offset-4">
            {t("new.goToPlan")}
          </Link>
        </Alert>
      ) : quota ? (
        <p className="text-sm text-muted" role="status">
          {quota.subscription_active
            ? t("header.planActive")
            : t("new.freeLeft", { n: quota.free_remaining, total: quota.free_limit })}
        </p>
      ) : null}

      <div className="grid items-start gap-6 lg:grid-cols-[minmax(0,1fr)_22rem]">
        <form
          className="grid gap-6"
          noValidate
          onSubmit={(event) => {
            event.preventDefault();
            void submitLive();
          }}
        >
          <Card className="grid gap-4 p-5 sm:p-6" aria-labelledby="step-tender">
            <h2 id="step-tender" className="text-lg font-semibold">
              <span className="mr-2 text-mint">1.</span>
              {t("new.stepTender")}
            </h2>
            <div className="grid gap-1.5">
              <Label htmlFor="tender-name">{t("tenderName")}</Label>
              <Input
                id="tender-name"
                name="label"
                value={label}
                placeholder={t("new.namePlaceholder")}
                maxLength={200}
                autoComplete="off"
                onChange={(event) => setLabel(event.target.value)}
              />
            </div>
            <div className="grid gap-2">
              <span className="text-sm font-medium">{t("tenderFile")}</span>
              <FilePicker
                label={t("tenderFile")}
                buttonLabel={tender ? t("new.replaceFile") : t("new.chooseTender")}
                hint={t("new.tenderHint", { mb: config?.limits.max_upload_mb ?? 15, pages: config?.limits.max_pages ?? 40 })}
                disabled={busy !== null}
                onFiles={([file]) => setTender(file)}
              />
              {tender ? (
                <p className="glass flex items-center gap-2 rounded-xl px-3 py-2 text-sm">
                  <FileText aria-hidden="true" className="size-4 shrink-0 text-mint" />
                  <span className="min-w-0 flex-1 truncate">{tender.name}</span>
                  <span className="text-xs text-muted">{formatBytes(tender.size)}</span>
                  <button
                    type="button"
                    className="grid size-8 place-items-center rounded-lg hover:bg-white/10"
                    aria-label={t("new.removeFile", { name: tender.name })}
                    onClick={() => setTender(null)}
                  >
                    <X aria-hidden="true" className="size-4" />
                  </button>
                </p>
              ) : null}
            </div>
          </Card>

          <Card className="grid gap-4 p-5 sm:p-6" aria-labelledby="step-company">
            <div>
              <h2 id="step-company" className="text-lg font-semibold">
                <span className="mr-2 text-mint">2.</span>
                {t("new.stepCompany")}
              </h2>
              <p className="mt-1 text-sm leading-6 text-muted">{t("new.companyHint")}</p>
            </div>

            {library && library.length > 0 ? (
              <fieldset className="grid gap-2">
                <legend className="mb-1 text-sm font-medium">{t("new.fromLibrary")}</legend>
                {library.map((document) => (
                  <label
                    key={document.document_id}
                    className="glass flex min-h-11 cursor-pointer items-center gap-3 rounded-xl px-3 py-2 text-sm"
                  >
                    <input
                      type="checkbox"
                      className="size-4 accent-blue-400"
                      checked={selected.includes(document.document_id)}
                      disabled={busy !== null}
                      onChange={() => toggle(document.document_id)}
                    />
                    <span className="min-w-0 flex-1 truncate">{document.original_name}</span>
                    <span className="text-xs text-muted">
                      {document.page_count} {t("pagesShort")}
                    </span>
                  </label>
                ))}
              </fieldset>
            ) : library ? (
              <p className="text-sm text-muted">
                {t("new.libraryEmpty")}{" "}
                <Link href="/documents" className="text-mint underline underline-offset-4">
                  {t("nav.documents")}
                </Link>
              </p>
            ) : null}

            <div className="grid gap-2">
              <span className="text-sm font-medium">{t("new.extraFiles")}</span>
              <FilePicker
                label={t("new.extraFiles")}
                buttonLabel={t("addFiles")}
                multiple
                disabled={busy !== null}
                onFiles={(files) => setExtra((current) => [...current, ...files].slice(0, 20))}
              />
              {extra.length > 0 ? (
                <ul className="grid gap-2">
                  {extra.map((file, index) => (
                    <li key={`${file.name}-${index}`} className="glass flex items-center gap-2 rounded-xl px-3 py-2 text-sm">
                      <Paperclip aria-hidden="true" className="size-4 shrink-0 text-mint" />
                      <span className="min-w-0 flex-1 truncate">{file.name}</span>
                      <span className="text-xs text-muted">{formatBytes(file.size)}</span>
                      <button
                        type="button"
                        className="grid size-8 place-items-center rounded-lg hover:bg-white/10"
                        aria-label={t("new.removeFile", { name: file.name })}
                        onClick={() => setExtra((current) => current.filter((_, position) => position !== index))}
                      >
                        <X aria-hidden="true" className="size-4" />
                      </button>
                    </li>
                  ))}
                </ul>
              ) : null}
              <p className="text-xs text-muted">
                {t("new.companyCount", { n: companyCount, max: limit })}
              </p>
            </div>
          </Card>

          <Card className="grid gap-4 p-5 sm:p-6" aria-labelledby="step-language">
            <h2 id="step-language" className="text-lg font-semibold">
              <span className="mr-2 text-mint">3.</span>
              {t("new.stepLanguage")}
            </h2>
            <div className="grid gap-1.5">
              <Label htmlFor="finding-language">{t("new.findingLanguage")}</Label>
              <select
                id="finding-language"
                value={findingLanguage}
                onChange={(event) => setLanguage(event.target.value as Locale)}
                className="min-h-10 w-full rounded-xl border border-white/15 bg-[#0c1c33] px-3 text-sm text-foreground focus-visible:outline-2 focus-visible:outline-mint"
              >
                {LOCALES.map((code) => (
                  <option key={code} value={code}>
                    {t(`languageName.${code}`)}
                  </option>
                ))}
              </select>
              <p className="text-xs leading-5 text-muted">{t("new.findingLanguageHint")}</p>
            </div>
          </Card>

          {error ? (
            <Alert tone="danger" role="alert">
              {error}
            </Alert>
          ) : null}

          <div className="flex flex-wrap items-center gap-3">
            <Button type="submit" size="lg" disabled={disabled || blockedByPlan}>
              {busy === "live" ? <Loader2 aria-hidden="true" className="size-5 animate-spin" /> : null}
              {busy === "live" ? t("working") : t("start")}
            </Button>
            <p className="text-xs leading-5 text-muted sm:max-w-sm">{t("new.sendNotice")}</p>
          </div>
        </form>

        <aside className="grid gap-4">
          <Card className="grid gap-3 p-5" aria-labelledby="sample-box">
            <Badge tone="uncertain" className="w-fit">
              <FlaskConical aria-hidden="true" className="size-3.5" />
              {t("sample.badge")}
            </Badge>
            <h2 id="sample-box" className="text-lg font-semibold">
              {t("sample.title")}
            </h2>
            <p className="text-sm leading-6 text-muted">{t("sample.body")}</p>
            <Button type="button" variant="outline" disabled={disabled} onClick={() => void submitSample()}>
              {busy === "sample" ? <Loader2 aria-hidden="true" className="size-4 animate-spin" /> : null}
              {busy === "sample" ? t("working") : t("sample.cta")}
            </Button>
          </Card>
          <Card className="p-5 text-sm leading-6 text-muted">
            <h2 className="font-semibold text-foreground">{t("new.privacyTitle")}</h2>
            <p className="mt-2">{t("new.privacyBody")}</p>
          </Card>
        </aside>
      </div>
    </div>
  );
}
