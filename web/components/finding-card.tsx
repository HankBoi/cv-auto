"use client";

import { StatusMark } from "@/components/status-mark";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Label } from "@/components/ui/label";
import { Textarea } from "@/components/ui/textarea";
import { analysisFileUrl, type Requirement } from "@/lib/api";
import { useErrorText } from "@/lib/hooks";
import { useI18n } from "@/lib/i18n";
import { cn } from "@/lib/utils";
import {
  AlertTriangle,
  BadgeCheck,
  Ban,
  CheckCircle2,
  CircleHelp,
  ExternalLink,
  FileX,
  Info,
  Lightbulb,
  MessageSquarePlus,
  Pencil,
  Save,
  ShieldAlert,
  TriangleAlert,
  XCircle,
  type LucideIcon,
} from "lucide-react";
import { useState } from "react";

type Tone = "high" | "medium" | "low" | "uncertain";

const idle = "border-white/15 bg-white/5 text-foreground hover:border-mint/50 hover:bg-white/10";
const DECISION_BUTTONS: {
  decision: string;
  key: string;
  icon: LucideIcon;
  activeClass: string;
  idleClass: string;
}[] = [
  { decision: "confirm", key: "confirm", icon: CheckCircle2, idleClass: "border-blue-400/40 bg-blue-400/10 text-blue-100 hover:bg-blue-400/20", activeClass: "border-blue-300 bg-blue-400 text-blue-950 shadow-[0_6px_20px_-8px_rgba(59,130,246,0.9)]" },
  { decision: "edit", key: "edit", icon: Pencil, idleClass: idle, activeClass: "border-mint bg-mint/25 text-white" },
  { decision: "reject", key: "reject", icon: XCircle, idleClass: "border-red-400/40 bg-red-400/10 text-red-100 hover:bg-red-400/20", activeClass: "border-red-300 bg-red-500 text-white" },
  { decision: "uncertain", key: "uncertain", icon: CircleHelp, idleClass: idle, activeClass: "border-amber-300 bg-amber-400 text-amber-950" },
  { decision: "missing", key: "missing", icon: FileX, idleClass: idle, activeClass: "border-mint bg-mint/25 text-white" },
  { decision: "not_applicable", key: "notApplicable", icon: Ban, idleClass: idle, activeClass: "border-mint bg-mint/25 text-white" },
];

const severityIcons: Record<Tone, LucideIcon> = {
  high: ShieldAlert,
  medium: TriangleAlert,
  low: Info,
  uncertain: CircleHelp,
};

const evidenceTone: Record<string, "mint" | "medium" | "uncertain" | "neutral"> = {
  possible_match: "mint",
  possible_mismatch: "medium",
  not_found: "neutral",
  expired_date_detected: "medium",
  unclear: "uncertain",
};

function reviewIcon(status: string) {
  if (status === "confirmed" || status === "changed") return "completed";
  if (status === "missing" || status === "expired" || status === "unclear" || status === "rejected") {
    return "completed_with_unresolved_items";
  }
  if (status === "not_applicable") return "cancelled";
  return "needs_review";
}

export function FindingCard({
  requirement,
  disabled,
  onSave,
  applicant,
}: {
  requirement: Requirement;
  disabled: boolean;
  onSave: (decision: string, statement?: string, note?: string) => Promise<void>;
  applicant?: { fileId: string; name: string } | null;
}) {
  const { t, has } = useI18n();
  const errorText = useErrorText();
  const [editing, setEditing] = useState(false);
  const [statement, setStatement] = useState(requirement.edited_statement || requirement.statement);
  const [note, setNote] = useState(requirement.human_note ?? "");
  const [localError, setLocalError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  const shown = requirement.edited_statement || requirement.statement;
  const severity = (requirement.quote_verified ? requirement.severity : "uncertain") as Tone;
  const SeverityIcon = severityIcons[severity] ?? CircleHelp;
  const label = (group: string, value: string) => (has(`${group}.${value}`) ? t(`${group}.${value}`) : value);

  async function save(decision: string) {
    if ((decision === "not_applicable" || decision === "comment") && note.trim().length === 0) {
      setLocalError(t(decision === "comment" ? "commentRequired" : "noteRequired"));
      return;
    }
    if (decision === "edit" && statement.trim().length === 0) {
      setLocalError(t("statementRequired"));
      return;
    }
    setLocalError(null);
    setSaving(true);
    try {
      await onSave(decision, decision === "edit" ? statement.trim() : undefined, note.trim() || undefined);
      if (decision === "edit") setEditing(false);
    } catch (caught) {
      setLocalError(errorText(caught));
    } finally {
      setSaving(false);
    }
  }

  const locked = disabled || saving;
  const headingId = `finding-${requirement.requirement_id}`;

  return (
    <Card className="p-4 sm:p-5" aria-labelledby={headingId}>
      <div className="flex flex-wrap items-center gap-2">
        <Badge tone={severity}>
          <SeverityIcon aria-hidden="true" className="size-3.5" />
          {t("severity.label")}: {label("severity", severity)}
        </Badge>
        <Badge>{label("kind", requirement.kind)}</Badge>
        <Badge>{label("category", requirement.category)}</Badge>
        <StatusMark status={reviewIcon(requirement.review_status)} label={label("review", requirement.review_status)} />
      </div>

      <h3 id={headingId} className="mt-3 text-lg leading-7 font-semibold">
        {shown}
      </h3>
      {requirement.edited_statement ? (
        <p className="mt-1 text-sm text-muted">
          <span className="font-medium text-foreground">{t("aiSuggestion")}: </span>
          {requirement.statement}
        </p>
      ) : (
        <p className="mt-1 text-xs text-muted">{t("aiSuggestion")}</p>
      )}
      {requirement.explanation ? <p className="mt-3 text-sm leading-6 text-foreground/90">{requirement.explanation}</p> : null}

      <figure className="mt-4 rounded-xl border border-white/12 bg-black/25 p-3 sm:p-4">
        <figcaption className="mb-2 flex flex-wrap items-center justify-between gap-2 text-xs text-muted">
          <span className="break-all">{requirement.source_file_name ?? t("sourceQuote")}</span>
          <span className="flex items-center gap-3">
            <span>
              {t("page")} {requirement.page_number ?? "—"}
            </span>
            {requirement.quote_verified && requirement.source_file_id && requirement.page_number ? (
              <a
                href={analysisFileUrl(requirement.source_file_id, requirement.page_number)}
                target="_blank"
                rel="noopener noreferrer"
                className="inline-flex min-h-8 items-center gap-1 text-mint underline-offset-4 hover:underline"
              >
                <ExternalLink aria-hidden="true" className="size-3.5" />
                {t("results.openPage")}
              </a>
            ) : null}
          </span>
        </figcaption>
        {requirement.quote_verified && requirement.quote ? (
          <>
            <blockquote className="font-serif text-base leading-7 break-words select-text">
              <mark className="quote-mark">{requirement.quote}</mark>
            </blockquote>
            <p className="mt-2 flex items-center gap-1.5 text-xs text-mint">
              <BadgeCheck aria-hidden="true" className="size-3.5" />
              {t("quoteVerified")}
            </p>
          </>
        ) : (
          <div role="note" className="grid gap-2 text-sm">
            <p className="flex items-start gap-2 font-medium text-amber-100">
              <AlertTriangle aria-hidden="true" className="mt-0.5 size-4 shrink-0" />
              {t("sourceNotVerified")}
            </p>
            <p className="leading-6 text-muted">{t("quoteUnverified")}</p>
            {requirement.quote ? (
              <details className="text-xs text-muted">
                <summary className="cursor-pointer">{t("showUnverified")}</summary>
                <p className="mt-1 leading-5 break-words italic">{requirement.quote}</p>
              </details>
            ) : null}
          </div>
        )}
      </figure>

      {requirement.possible_impact || requirement.next_step ? (
        <dl className="mt-4 grid gap-3 text-sm sm:grid-cols-2">
          {requirement.possible_impact ? (
            <div>
              <dt className="text-xs font-medium tracking-wide text-muted uppercase">{t("possibleImpact")}</dt>
              <dd className="mt-1 leading-6">{requirement.possible_impact}</dd>
            </div>
          ) : null}
          {requirement.next_step ? (
            <div>
              <dt className="flex items-center gap-1 text-xs font-medium tracking-wide text-muted uppercase">
                <Lightbulb aria-hidden="true" className="size-3.5" />
                {t("nextStep")}
              </dt>
              <dd className="mt-1 leading-6">{requirement.next_step}</dd>
            </div>
          ) : null}
        </dl>
      ) : null}

      {applicant ? <p className="mt-4 text-sm font-semibold">{applicant.name}</p> : null}
      {(applicant
        ? (requirement.evidence ?? []).filter((evidence) => evidence.evidence_file_id === applicant.fileId)
        : (requirement.evidence ?? [])
      ).map((evidence, index) => (
        <div key={index} className="mt-2 border-l-2 border-accent pl-3 text-sm">
          <Badge tone={evidenceTone[evidence.status] ?? "neutral"}>{label("evidenceStatus", evidence.status)}</Badge>
          {evidence.evidence_file_name ? (
            <p className="mt-1 text-muted">
              {applicant ? applicant.name : evidence.evidence_file_name}
              {evidence.page_number ? `, ${t("page")} ${evidence.page_number}` : ""}
              {applicant ? ` · ${evidence.evidence_file_name}` : ""}
            </p>
          ) : null}
          {evidence.quote_verified && evidence.quote ? (
            <p className="mt-2 font-serif leading-6 select-text">{evidence.quote}</p>
          ) : evidence.quote ? (
            <p className="mt-2 text-xs text-amber-200">{t("sourceNotVerified")}</p>
          ) : null}
          {evidence.explanation && evidence.status !== "not_found" ? (
            <p className="mt-2 leading-6 text-muted">{evidence.explanation}</p>
          ) : null}
          {evidence.status === "not_found" ? <p className="mt-1 text-muted">{t("notFoundDetail")}</p> : null}
          {evidence.date_observation ? <p className="mt-2">{evidence.date_observation}</p> : null}
        </div>
      ))}
      {applicant && !(requirement.evidence ?? []).some((evidence) => evidence.evidence_file_id === applicant.fileId) ? (
        <p className="mt-4 border-l-2 border-white/20 pl-3 text-sm text-muted">{t("applicant.notInCv", { name: applicant.name })}</p>
      ) : null}

      {requirement.review_reason && requirement.quote_verified ? (
        <p className="mt-3 text-xs text-muted">
          {t("reviewReason")}: {requirement.review_reason}
        </p>
      ) : null}

      <div className="mt-4 grid gap-3 border-t border-white/10 pt-4">
        <p className="text-sm font-medium">
          {t("humanDecision")}
          {requirement.reviewer_decision ? (
            <span className="ml-2 font-normal text-mint">· {label("decision", requirement.reviewer_decision)}</span>
          ) : null}
        </p>
        {editing ? (
          <div className="grid gap-2">
            <Label htmlFor={`edit-${requirement.requirement_id}`}>{t("edit")}</Label>
            <Textarea id={`edit-${requirement.requirement_id}`} value={statement} onChange={(event) => setStatement(event.target.value)} />
            <div className="flex gap-2">
              <Button type="button" disabled={locked} onClick={() => void save("edit")}>
                <Save aria-hidden="true" className="size-4" />
                {t("save")}
              </Button>
              <Button type="button" variant="outline" onClick={() => setEditing(false)}>
                {t("cancelEdit")}
              </Button>
            </div>
          </div>
        ) : null}
        <div className="grid grid-cols-2 gap-2 sm:grid-cols-3 lg:grid-cols-6" role="group" aria-label={t("humanDecision")}>
          {DECISION_BUTTONS.map(({ decision, key, icon: Icon, activeClass, idleClass }) => {
            const active = requirement.reviewer_decision === decision;
            return (
              <button
                key={decision}
                type="button"
                aria-pressed={active}
                disabled={locked}
                onClick={() => (decision === "edit" ? setEditing(true) : void save(decision))}
                className={cn(
                  "inline-flex min-h-11 items-center justify-center gap-1.5 rounded-xl border px-2.5 py-2 text-center text-xs leading-tight font-medium transition-all duration-150",
                  "hover:-translate-y-0.5 active:translate-y-0 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-mint",
                  "disabled:pointer-events-none disabled:opacity-50",
                  active ? activeClass : idleClass,
                )}
              >
                <Icon aria-hidden="true" className="size-4 shrink-0" />
                <span>{t(key)}</span>
              </button>
            );
          })}
        </div>
        <div className="grid gap-2">
          <Label htmlFor={`note-${requirement.requirement_id}`}>{t("note")}</Label>
          <Textarea id={`note-${requirement.requirement_id}`} value={note} onChange={(event) => setNote(event.target.value)} />
          <div>
            <Button type="button" variant="outline" disabled={locked} onClick={() => void save("comment")} className="w-full sm:w-auto">
              <MessageSquarePlus aria-hidden="true" className="size-4" />
              {t("saveComment")}
            </Button>
          </div>
        </div>
        {localError ? (
          <p className="text-sm text-danger" role="alert">
            {localError}
          </p>
        ) : null}
      </div>
    </Card>
  );
}
