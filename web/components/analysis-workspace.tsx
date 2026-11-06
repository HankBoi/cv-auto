"use client";

import { FilePicker } from "@/components/file-picker";
import { FindingCard } from "@/components/finding-card";
import { StatusMark } from "@/components/status-mark";
import { Alert } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button, buttonVariants } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Label } from "@/components/ui/label";
import {
  addEvidence,
  analysisFileUrl,
  cancelAnalysis,
  deleteAnalysis,
  fetchDocumentFile,
  getAnalysis,
  getPages,
  getRequirements,
  listDocuments,
  matchEvidence,
  patchRequirement,
  retryAnalysis,
  type Analysis,
  type LibraryDocument,
  type PagePreview,
  type Requirement,
} from "@/lib/api";
import { useErrorText } from "@/lib/hooks";
import { useI18n } from "@/lib/i18n";
import { cn } from "@/lib/utils";
import {
  ArrowLeft,
  Download,
  ExternalLink,
  FlaskConical,
  Loader2,
  ShieldAlert,
  Trash2,
} from "lucide-react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useCallback, useEffect, useMemo, useState } from "react";

const active = new Set(["queued", "processing"]);
const CATEGORIES = [
  "required_document",
  "deadline",
  "technical_mismatch",
  "contract_terms",
  "conflicting_unclear",
  "other",
];
const SEVERITIES = ["high", "medium", "low", "uncertain"];

function fileStem(name: string) {
  return name.replace(/\.pdf$/i, "").replace(/[-_]+/g, " ").trim();
}

function applicantName(file: { file_id: string; original_name: string }, names: Record<string, string>) {
  const custom = names[file.file_id]?.trim();
  return custom || fileStem(file.original_name) || file.original_name;
}

const selectClass =
  "min-h-10 w-full rounded-xl border border-white/15 bg-[#0c1c33] px-3 text-sm text-foreground focus-visible:outline-2 focus-visible:outline-mint";

export function AnalysisWorkspace({ id }: { id: string }) {
  const { t, has } = useI18n();
  const errorText = useErrorText();
  const router = useRouter();
  const [analysis, setAnalysis] = useState<Analysis | null>(null);
  const [requirements, setRequirements] = useState<Requirement[]>([]);
  const [pages, setPages] = useState<PagePreview[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [confirmingDelete, setConfirmingDelete] = useState(false);
  const [tick, setTick] = useState(0);
  const [category, setCategory] = useState("all");
  const [severity, setSeverity] = useState("all");
  const [onlyUnverified, setOnlyUnverified] = useState(false);
  const [onlyOpen, setOnlyOpen] = useState(false);
  const [applicantNames, setApplicantNames] = useState<Record<string, string>>({});
  const [applicantId, setApplicantId] = useState<string | null>(null);

  const refresh = useCallback(() => setTick((value) => value + 1), []);

  const evidenceFiles = useMemo(
    () => (analysis?.files ?? []).filter((file) => file.role === "evidence"),
    [analysis],
  );

  useEffect(() => {
    if (!analysis) return;
    const key = `statific-applicants:${analysis.analysis_id}`;
    let stored: Record<string, string> = {};
    try {
      const parsed = JSON.parse(window.localStorage.getItem(key) ?? "{}") as unknown;
      if (parsed && typeof parsed === "object") stored = parsed as Record<string, string>;
    } catch {
      stored = {};
    }
    setApplicantNames(stored);
    const files = analysis.files.filter((file) => file.role === "evidence");
    setApplicantId((current) => (current && files.some((file) => file.file_id === current) ? current : (files[0]?.file_id ?? null)));
  }, [analysis]);

  function renameApplicant(fileId: string, value: string) {
    if (!analysis) return;
    setApplicantNames((current) => {
      const next = { ...current, [fileId]: value };
      window.localStorage.setItem(`statific-applicants:${analysis.analysis_id}`, JSON.stringify(next));
      return next;
    });
  }

  useEffect(() => {
    let stop = false;
    let timer = 0;

    async function load() {
      try {
        const next = await getAnalysis(id);
        if (stop) return;
        setAnalysis(next);
        setError(null);
        const [requirementBody, pageBody] = await Promise.all([getRequirements(id), getPages(id)]);
        if (stop) return;
        setRequirements(requirementBody.requirements);
        setPages(pageBody.pages);
        if (active.has(next.status)) {
          timer = window.setTimeout(load, 1500);
        }
      } catch (caught) {
        if (!stop) setError(errorText(caught));
      }
    }

    void load();
    return () => {
      stop = true;
      window.clearTimeout(timer);
    };
  }, [id, tick, errorText]);

  async function run(action: () => Promise<void>) {
    setBusy(true);
    setError(null);
    setNotice(null);
    try {
      await action();
      refresh();
    } catch (caught) {
      setError(errorText(caught));
    } finally {
      setBusy(false);
    }
  }

  const visible = useMemo(
    () =>
      requirements.filter((item) => {
        if (category !== "all" && item.category !== category) return false;
        if (severity !== "all" && (item.quote_verified ? item.severity : "uncertain") !== severity) return false;
        if (onlyUnverified && item.quote_verified) return false;
        if (onlyOpen && item.reviewer_decision) return false;
        return true;
      }),
    [requirements, category, severity, onlyUnverified, onlyOpen],
  );

  const working = analysis ? active.has(analysis.status) : false;
  const hasEvidence = analysis?.files.some((file) => file.role === "evidence") ?? false;
  const filtersOn = category !== "all" || severity !== "all" || onlyUnverified || onlyOpen;

  return (
    <div className="grid gap-6">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="min-w-0">
          <Link href="/analyses" className="inline-flex items-center gap-1.5 text-sm text-mint underline-offset-4 hover:underline">
            <ArrowLeft aria-hidden="true" className="size-4" />
            {t("results.back")}
          </Link>
          <h1 className="mt-2 text-2xl font-semibold tracking-tight break-words sm:text-3xl">
            {analysis?.label ?? t("loading")}
          </h1>
        </div>
        {analysis ? (
          <div className="flex flex-wrap items-center gap-2" role="status" aria-live="polite">
            {analysis.is_sample ? (
              <Badge tone="uncertain">
                <FlaskConical aria-hidden="true" className="size-3.5" />
                {t("sample.badge")}
              </Badge>
            ) : null}
            <StatusMark status={analysis.status} label={t(`status.${analysis.status}`)} />
            <span className="text-sm text-muted">
              {t("progressLabel")}: {has(`progress.${analysis.progress_stage}`) ? t(`progress.${analysis.progress_stage}`) : analysis.progress_stage}
            </span>
          </div>
        ) : null}
      </div>

      {error ? (
        <Alert tone="danger" role="alert">
          {error}
        </Alert>
      ) : null}
      {notice ? <Alert tone="success">{notice}</Alert> : null}
      {!analysis && !error ? (
        <Alert className="flex items-center gap-2">
          <Loader2 aria-hidden="true" className="size-4 animate-spin" />
          {t("loading")}
        </Alert>
      ) : null}

      {analysis ? (
        <>
          <Notices analysis={analysis} working={working} />

          <div className="grid items-start gap-6 lg:grid-cols-[minmax(0,1fr)_21rem]">
            <section className="grid gap-4" aria-labelledby="findings-heading">
              <SummaryPanel analysis={analysis} />

              <div className="flex flex-wrap items-center justify-between gap-2">
                <h2 id="findings-heading" className="text-xl font-semibold">
                  {t("results.findings")}
                  <span className="ml-2 text-sm font-normal text-muted">
                    {filtersOn ? `${visible.length}/${requirements.length}` : requirements.length}
                  </span>
                </h2>
                <div className="flex flex-wrap gap-2">
                  {working ? (
                    <Button type="button" variant="outline" disabled={busy} onClick={() => void run(() => cancelAnalysis(id).then(() => undefined))}>
                      {t("cancel")}
                    </Button>
                  ) : null}
                  {analysis.status === "failed" ? (
                    <Button type="button" disabled={busy} onClick={() => void run(() => retryAnalysis(id).then(() => undefined))}>
                      {t("retry")}
                    </Button>
                  ) : null}
                  {requirements.length > 0 && !working ? (
                    <Link href={`/analyses/${id}/export`} className={cn(buttonVariants({ variant: "outline" }))}>
                      <Download aria-hidden="true" className="size-4" />
                      {t("export")}
                    </Link>
                  ) : null}
                </div>
              </div>

              {requirements.length > 0 ? (
                <Card className="grid gap-3 p-4 sm:grid-cols-2" aria-label={t("filters.title")}>
                  <div className="grid gap-1.5">
                    <Label htmlFor="filter-category">{t("filters.category")}</Label>
                    <select id="filter-category" className={selectClass} value={category} onChange={(event) => setCategory(event.target.value)}>
                      <option value="all">{t("filters.all")}</option>
                      {CATEGORIES.map((value) => (
                        <option key={value} value={value}>
                          {t(`category.${value}`)}
                        </option>
                      ))}
                    </select>
                  </div>
                  <div className="grid gap-1.5">
                    <Label htmlFor="filter-severity">{t("filters.severity")}</Label>
                    <select id="filter-severity" className={selectClass} value={severity} onChange={(event) => setSeverity(event.target.value)}>
                      <option value="all">{t("filters.all")}</option>
                      {SEVERITIES.map((value) => (
                        <option key={value} value={value}>
                          {t(`severity.${value}`)}
                        </option>
                      ))}
                    </select>
                  </div>
                  <label className="flex min-h-10 items-center gap-2 text-sm">
                    <input type="checkbox" className="size-4 accent-blue-400" checked={onlyUnverified} onChange={(event) => setOnlyUnverified(event.target.checked)} />
                    {t("filters.unverified")}
                  </label>
                  <label className="flex min-h-10 items-center gap-2 text-sm">
                    <input type="checkbox" className="size-4 accent-blue-400" checked={onlyOpen} onChange={(event) => setOnlyOpen(event.target.checked)} />
                    {t("filters.open")}
                  </label>
                </Card>
              ) : null}

              {analysis.error ? (
                <Alert tone="danger" role="alert">
                  {has(`errors.${analysis.error.code}`) ? t(`errors.${analysis.error.code}`) : t("errors.generic")}
                </Alert>
              ) : null}
              {requirements.length === 0 && !working && analysis.status !== "failed" ? (
                <Alert tone="warn">{t("noRequirements")}</Alert>
              ) : null}
              {requirements.length > 0 && visible.length === 0 ? <Alert>{t("filters.none")}</Alert> : null}

              {evidenceFiles.length > 0 && applicantId ? (
                <Card className="grid gap-3 p-4" aria-labelledby="applicant-heading">
                  <div>
                    <h2 id="applicant-heading" className="font-semibold">
                      {t("applicant.title")}
                    </h2>
                    <p className="mt-1 text-sm leading-6 text-muted">{t("applicant.hint")}</p>
                  </div>
                  <div className="flex flex-wrap gap-2" role="radiogroup" aria-label={t("applicant.title")}>
                    {evidenceFiles.map((file) => {
                      const name = applicantName(file, applicantNames);
                      const selected = file.file_id === applicantId;
                      return (
                        <button
                          key={file.file_id}
                          type="button"
                          role="radio"
                          aria-checked={selected}
                          onClick={() => setApplicantId(file.file_id)}
                          className={cn(
                            "min-h-10 rounded-full border px-3 text-sm",
                            selected ? "border-blue-300 bg-blue-400 text-blue-950" : "border-white/15 bg-white/5 hover:bg-white/10",
                          )}
                        >
                          {name}
                        </button>
                      );
                    })}
                  </div>
                  <Label htmlFor="applicant-name">{t("applicant.name")}</Label>
                  <input
                    id="applicant-name"
                    value={applicantNames[applicantId] ?? ""}
                    placeholder={fileStem(evidenceFiles.find((file) => file.file_id === applicantId)?.original_name ?? "")}
                    onChange={(event) => renameApplicant(applicantId, event.target.value)}
                    className={selectClass}
                  />
                  <p className="text-sm font-medium">
                    {t("applicant.meets", {
                      name: applicantName(evidenceFiles.find((file) => file.file_id === applicantId)!, applicantNames),
                      matched: requirements.filter((item) =>
                        item.evidence?.some(
                          (evidence) => evidence.evidence_file_id === applicantId && evidence.status === "possible_match",
                        ),
                      ).length,
                      total: requirements.length,
                    })}
                  </p>
                </Card>
              ) : null}

              <ul className="grid gap-4">
                {visible.map((requirement) => (
                  <li key={requirement.requirement_id}>
                    <FindingCard
                      requirement={requirement}
                      applicant={
                        applicantId
                          ? {
                              fileId: applicantId,
                              name: applicantName(
                                evidenceFiles.find((file) => file.file_id === applicantId) ?? {
                                  file_id: applicantId,
                                  original_name: "",
                                },
                                applicantNames,
                              ),
                            }
                          : null
                      }
                      disabled={busy}
                      onSave={async (decision, statement, note) => {
                        const saved = await patchRequirement(requirement.requirement_id, decision, statement, note);
                        setRequirements((current) =>
                          current.map((item) => (item.requirement_id === saved.requirement_id ? saved : item)),
                        );
                        setNotice(t("saved"));
                        refresh();
                      }}
                    />
                  </li>
                ))}
              </ul>
            </section>

            <aside className="grid min-w-0 gap-4 lg:sticky lg:top-20 lg:max-h-[calc(100vh-6rem)] lg:overflow-y-auto lg:overscroll-contain lg:pr-1 [scrollbar-width:thin]">
              <EvidencePanel
                busy={busy}
                blocked={working || analysis.status === "failed" || analysis.status === "cancelled"}
                canMatch={analysis.counts.requirements > 0 && hasEvidence}
                onUpload={(files) =>
                  run(async () => {
                    await addEvidence(id, files);
                    setNotice(t("results.evidenceAdded"));
                  })
                }
                onLibrary={(documents) =>
                  run(async () => {
                    const files = await Promise.all(documents.map((document) => fetchDocumentFile(document)));
                    await addEvidence(id, files);
                    setNotice(t("results.evidenceAdded"));
                  })
                }
                onMatch={() =>
                  run(async () => {
                    await matchEvidence(id);
                  })
                }
              />
              <FileList analysis={analysis} />
              <PageList pages={pages} />
              <Card className="p-4">
                {confirmingDelete ? (
                  <div className="grid gap-3">
                    <p className="text-sm leading-6">{t("confirmDelete")}</p>
                    <div className="flex flex-wrap gap-2">
                      <Button
                        type="button"
                        variant="danger"
                        disabled={busy}
                        onClick={() =>
                          void run(async () => {
                            await deleteAnalysis(id);
                            router.push("/analyses");
                          })
                        }
                      >
                        {t("delete")}
                      </Button>
                      <Button type="button" variant="outline" onClick={() => setConfirmingDelete(false)}>
                        {t("keep")}
                      </Button>
                    </div>
                  </div>
                ) : (
                  <Button type="button" variant="danger" onClick={() => setConfirmingDelete(true)}>
                    <Trash2 aria-hidden="true" className="size-4" />
                    {t("delete")}
                  </Button>
                )}
              </Card>
            </aside>
          </div>
        </>
      ) : null}
    </div>
  );
}

function Notices({ analysis, working }: { analysis: Analysis; working: boolean }) {
  const { t } = useI18n();
  const warnings = analysis.warnings ?? [];
  const unreadable = warnings.filter((item) => item.startsWith("unreadable:"));
  const duplicate = warnings.some((item) => item.startsWith("duplicate:"));

  return (
    <div className="grid gap-2">
      <Alert tone="warn" className="flex items-start gap-2">
        <ShieldAlert aria-hidden="true" className="mt-0.5 size-4 shrink-0" />
        <span>{t("results.aiBanner")}</span>
      </Alert>
      {analysis.is_sample ? <Alert>{t("sample.banner")}</Alert> : null}
      {working ? (
        <Alert className="flex items-center gap-2" role="status">
          <Loader2 aria-hidden="true" className="size-4 shrink-0 animate-spin" />
          {t("results.working")}
        </Alert>
      ) : null}
      {analysis.status === "queued" && analysis.stale ? <Alert tone="warn">{t("waitingWorker")}</Alert> : null}
      {analysis.stale && analysis.status !== "queued" ? <Alert tone="warn">{t("stale")}</Alert> : null}
      {analysis.model_id ? (
        <p className="text-xs text-muted">
          {t("liveModel")}: {analysis.model_id} · {t("results.language")}: {t(`languageName.${analysis.language}`)}
        </p>
      ) : null}
      {unreadable.length > 0 ? (
        <Alert tone="warn">
          <p className="font-medium">{t("unreadable")}</p>
          <ul className="mt-1 list-disc pl-5 text-amber-50/90">
            {unreadable.map((item) => {
              const [, name, page] = item.split(":");
              return (
                <li key={item}>
                  {name}, {t("page")} {page?.replace("p", "")}
                </li>
              );
            })}
          </ul>
        </Alert>
      ) : null}
      {duplicate ? <Alert tone="warn">{t("duplicate")}</Alert> : null}
    </div>
  );
}

function SummaryPanel({ analysis }: { analysis: Analysis }) {
  const { t } = useI18n();
  const summary = analysis.summary;
  if (!summary || summary.findings_total === 0) return null;

  return (
    <Card className="grid gap-4 p-5" aria-labelledby="summary-heading">
      <h2 id="summary-heading" className="text-lg font-semibold">
        {t("summary.title")}
      </h2>
      <dl className="grid grid-cols-2 gap-3 text-sm sm:grid-cols-4">
        <Tile label={t("summary.total")} value={summary.findings_total} />
        <Tile label={t("summary.verified")} value={summary.source_verified} />
        <Tile label={t("summary.unverified")} value={summary.source_not_verified} warn={summary.source_not_verified > 0} />
        <Tile label={t("summary.reviewed")} value={`${summary.reviewed}/${summary.findings_total}`} />
      </dl>
      <div className="flex flex-wrap gap-2" aria-label={t("filters.severity")}>
        {SEVERITIES.map((value) => (
          <Badge key={value} tone={value as "high" | "medium" | "low" | "uncertain"}>
            {t(`severity.${value}`)}: {summary.by_severity[value as keyof typeof summary.by_severity]}
          </Badge>
        ))}
      </div>
      {summary.evidence.possible_match +
        summary.evidence.possible_mismatch +
        summary.evidence.not_found_in_uploaded_files +
        summary.evidence.expiry_date_seen +
        summary.evidence.unclear >
      0 ? (
        <div>
          <h3 className="mb-2 text-sm font-medium">{t("summary.evidence")}</h3>
          <dl className="grid grid-cols-2 gap-3 text-sm sm:grid-cols-4">
            <Tile label={t("evidenceStatus.possible_match")} value={summary.evidence.possible_match} />
            <Tile label={t("evidenceStatus.possible_mismatch")} value={summary.evidence.possible_mismatch} />
            <Tile label={t("evidenceStatus.not_found")} value={summary.evidence.not_found_in_uploaded_files} />
            <Tile label={t("evidenceStatus.expired_date_detected")} value={summary.evidence.expiry_date_seen} />
          </dl>
        </div>
      ) : null}
      <p className="text-xs leading-5 text-muted">{t("summary.note")}</p>
    </Card>
  );
}

function Tile({ label, value, warn = false }: { label: string; value: string | number; warn?: boolean }) {
  return (
    <div className={cn("rounded-xl border bg-black/20 px-3 py-2", warn ? "border-amber-300/50" : "border-white/10")}>
      <dt className="text-xs text-muted">{label}</dt>
      <dd className="mt-0.5 text-lg font-semibold">{value}</dd>
    </div>
  );
}

function EvidencePanel({
  busy,
  blocked,
  canMatch,
  onUpload,
  onLibrary,
  onMatch,
}: {
  busy: boolean;
  blocked: boolean;
  canMatch: boolean;
  onUpload: (files: File[]) => void;
  onLibrary: (documents: LibraryDocument[]) => void;
  onMatch: () => void;
}) {
  const { t } = useI18n();
  const [library, setLibrary] = useState<LibraryDocument[]>([]);
  const [picked, setPicked] = useState<string[]>([]);

  useEffect(() => {
    let stop = false;
    listDocuments()
      .then((body) => {
        if (!stop) setLibrary(body.documents);
      })
      .catch(() => undefined);
    return () => {
      stop = true;
    };
  }, []);

  return (
    <Card className="grid gap-3 p-4" aria-labelledby="evidence-heading">
      <div>
        <h2 id="evidence-heading" className="font-semibold">
          {t("evidenceTitle")}
        </h2>
        <p className="mt-1 text-sm leading-6 text-muted">{t("evidenceHint")}</p>
      </div>
      <FilePicker label={t("addFiles")} multiple disabled={busy || blocked} onFiles={onUpload} />

      {library.length > 0 ? (
        <details className="rounded-xl border border-white/10 bg-black/20 p-3">
          <summary className="cursor-pointer text-sm font-medium">{t("results.fromLibrary")}</summary>
          <ul className="mt-2 grid gap-1.5">
            {library.map((document) => (
              <li key={document.document_id}>
                <label className="flex min-h-9 items-center gap-2 text-sm">
                  <input
                    type="checkbox"
                    className="size-4 accent-blue-400"
                    checked={picked.includes(document.document_id)}
                    onChange={() =>
                      setPicked((current) =>
                        current.includes(document.document_id)
                          ? current.filter((item) => item !== document.document_id)
                          : [...current, document.document_id],
                      )
                    }
                  />
                  <span className="min-w-0 flex-1 truncate">{document.original_name}</span>
                </label>
              </li>
            ))}
          </ul>
          <Button
            type="button"
            size="sm"
            variant="outline"
            className="mt-2"
            disabled={busy || blocked || picked.length === 0}
            onClick={() => {
              const chosen = library.filter((document) => picked.includes(document.document_id));
              setPicked([]);
              onLibrary(chosen);
            }}
          >
            {t("results.addSelected")}
          </Button>
        </details>
      ) : null}

      <Button type="button" disabled={busy || blocked || !canMatch} onClick={onMatch}>
        {busy ? <Loader2 aria-hidden="true" className="size-4 animate-spin" /> : null}
        {t("match")}
      </Button>
      {!canMatch && !blocked ? <p className="text-xs leading-5 text-muted">{t("results.matchHint")}</p> : null}
      <p className="text-xs leading-5 text-muted">{t("results.matchWording")}</p>
    </Card>
  );
}

function FileList({ analysis }: { analysis: Analysis }) {
  const { t } = useI18n();
  return (
    <Card className="p-4" aria-labelledby="files-heading">
      <h2 id="files-heading" className="font-semibold">
        {t("filesHeading")}
      </h2>
      <ul className="mt-3 grid gap-3 text-sm">
        {analysis.files.map((file) => (
          <li key={file.file_id} className="border-t border-white/10 pt-3 first:border-0 first:pt-0">
            <p className="font-medium break-all">{file.original_name}</p>
            <p className="text-muted">
              {file.role === "tender" ? t("tenderRole") : t("evidenceRole")} · {file.page_count} {t("pagesShort")}
            </p>
            {file.duplicate_of_file_id ? <p className="text-amber-200">{t("duplicate")}</p> : null}
            {file.unreadable_pages && file.unreadable_pages.length > 0 ? (
              <p className="text-amber-200">
                {t("notReadable")}: {file.unreadable_pages.join(", ")}
              </p>
            ) : null}
            <a
              href={analysisFileUrl(file.file_id)}
              target="_blank"
              rel="noopener noreferrer"
              className="mt-1 inline-flex min-h-8 items-center gap-1 text-mint underline-offset-4 hover:underline"
            >
              <ExternalLink aria-hidden="true" className="size-3.5" />
              {t("results.openPdf")}
            </a>
          </li>
        ))}
      </ul>
    </Card>
  );
}

function PageList({ pages }: { pages: PagePreview[] }) {
  const { t } = useI18n();
  if (pages.length === 0) return null;
  return (
    <Card className="min-w-0 p-4">
      <details className="min-w-0">
        <summary className="cursor-pointer font-semibold">
          {t("pages")} ({pages.length})
        </summary>
        <ul className="mt-3 grid max-h-[24rem] min-w-0 gap-3 overflow-x-hidden overflow-y-auto pr-1 text-sm [overflow-wrap:anywhere]">
          {pages.map((page) => (
            <li key={`${page.file_id}-${page.page_number}`} className="border-t border-white/10 pt-3 first:border-0 first:pt-0">
              <p className="font-medium break-all">
                {page.file_name} · {t("page")} {page.page_number}
              </p>
              <p className="text-muted">{page.usable ? t("readable") : t("notReadable")}</p>
              {page.usable && page.preview ? (
                <p className="mt-1 line-clamp-6 leading-6 break-words select-text">{page.preview}</p>
              ) : (
                <p className="mt-1 text-amber-200">{t("unreadable")}</p>
              )}
            </li>
          ))}
        </ul>
      </details>
    </Card>
  );
}
