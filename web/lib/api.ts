// Empty means "same host as this page". Next forwards /api to the API process.
// A browser on another machine must not call 127.0.0.1:43124 directly.
export const API_BASE = process.env.NEXT_PUBLIC_API_BASE_URL ?? "";

export class ApiError extends Error {
  code: string;
  retryable: boolean;
  status: number;

  constructor(code: string, message: string, retryable: boolean, status: number) {
    super(message);
    this.code = code;
    this.retryable = retryable;
    this.status = status;
  }
}

export type StoredFile = {
  file_id: string;
  original_name: string;
  role: string;
  size_bytes: number;
  page_count: number;
  sha256: string;
  status: string;
  duplicate_of_file_id?: string | null;
  unreadable_pages?: number[];
};

export type Summary = {
  findings_total: number;
  source_verified: number;
  source_not_verified: number;
  requirements: number;
  risks: number;
  reviewed: number;
  by_severity: { high: number; medium: number; low: number; uncertain: number };
  by_category: Record<string, number>;
  evidence: {
    possible_match: number;
    possible_mismatch: number;
    not_found_in_uploaded_files: number;
    expiry_date_seen: number;
    unclear: number;
  };
};

export type Analysis = {
  analysis_id: string;
  label: string;
  language: string;
  is_sample: boolean;
  quota_counted: boolean;
  created_at: string;
  updated_at: string;
  status: string;
  progress_stage: string;
  provider_id?: string | null;
  model_id?: string | null;
  prompt_version?: string | null;
  stale: boolean;
  counts: {
    files: number;
    pages: number;
    usable_pages: number;
    unreadable_pages: number;
    requirements: number;
  };
  summary: Summary;
  error?: { code: string; user_message: string; retryable: boolean } | null;
  warnings: string[];
  files: StoredFile[];
};

export type AnalysisListItem = {
  analysis_id: string;
  label: string;
  status: string;
  progress_stage: string;
  created_at: string;
  updated_at: string;
  is_sample: boolean;
  language: string;
  error_code?: string | null;
  model_id?: string | null;
  files: number;
  findings: number;
  reviewed: number;
  source_not_verified: number;
};

export type EvidenceMatch = {
  evidence_file_id?: string | null;
  evidence_file_name?: string | null;
  page_number?: number | null;
  quote?: string | null;
  quote_verified: boolean;
  explanation?: string | null;
  date_observation?: string | null;
  status: string;
  needs_human_review: boolean;
  review_reason?: string | null;
};

export type Requirement = {
  requirement_id: string;
  kind: string;
  category: string;
  severity: string;
  explanation?: string | null;
  possible_impact?: string | null;
  next_step?: string | null;
  statement: string;
  edited_statement?: string | null;
  requirement_class: string;
  mandatory_label: string;
  due_date_text?: string | null;
  source_file_id?: string | null;
  source_file_name?: string | null;
  page_number?: number | null;
  quote?: string | null;
  quote_verified: boolean;
  needs_human_review: boolean;
  review_reason?: string | null;
  review_status: string;
  reviewer_decision?: string | null;
  human_note?: string | null;
  reviewed_at?: string | null;
  evidence: EvidenceMatch[];
};

export type PagePreview = {
  file_id: string;
  file_name: string;
  role: string;
  page_number: number;
  usable: boolean;
  extraction_method: string;
  ocr_status: string;
  preview: string;
};

export type LibraryDocument = {
  document_id: string;
  original_name: string;
  size_bytes: number;
  page_count: number;
  created_at: string;
};

export type Quota = {
  free_limit: number;
  free_used: number;
  free_remaining: number;
  subscription_active: boolean;
  subscription_status: string;
  subscription_expires_at?: string | null;
  can_start: boolean;
  payments_mode: string;
  demo_price_azn: number;
  price_is_demo: boolean;
  real_payment_processed: boolean;
};

export type AppConfig = {
  model_configured: boolean;
  provider_id?: string | null;
  model_id?: string | null;
  payments_mode: string;
  ocr_enabled: boolean;
  limits: {
    max_upload_mb: number;
    max_pages: number;
    max_company_documents_per_analysis: number;
    max_library_documents: number;
  };
};

export type RejectedUpload = { original_name: string; code: string; user_message: string };

async function readError(response: Response): Promise<ApiError> {
  try {
    const body = (await response.json()) as {
      code?: string;
      user_message?: string;
      retryable?: boolean;
    };
    return new ApiError(
      body.code ?? "generic",
      body.user_message ?? "Request failed",
      Boolean(body.retryable),
      response.status,
    );
  } catch {
    return new ApiError("generic", "Request failed", false, response.status);
  }
}

async function send<T>(path: string, init?: RequestInit): Promise<T> {
  let response: Response;
  try {
    response = await fetch(`${API_BASE}${path}`, {
      ...init,
      headers: init?.body instanceof FormData ? init.headers : { ...init?.headers },
    });
  } catch {
    throw new ApiError("api_unreachable", "The API did not respond.", true, 0);
  }
  if (!response.ok) {
    throw await readError(response);
  }
  if (response.status === 204) {
    return undefined as T;
  }
  const text = await response.text();
  return text ? (JSON.parse(text) as T) : (undefined as T);
}

export function getHealth() {
  return send<{ status: string }>("/api/health");
}

export function getConfig() {
  return send<AppConfig>("/api/config");
}

export function getQuota() {
  return send<Quota>("/api/quota");
}

export function activateDemoPlan() {
  return send<Quota>("/api/subscription/demo-activate", { method: "POST" });
}

export type CreateOptions = {
  language: string;
  sample?: boolean;
  documentIds?: string[];
  files?: File[];
};

export function createAnalysis(label: string, file: File, options: CreateOptions) {
  const body = new FormData();
  body.set("label", label);
  body.set("language", options.language);
  if (options.sample) {
    body.set("sample", "true");
  }
  for (const id of options.documentIds ?? []) {
    body.append("document_ids", id);
  }
  body.set("file", file);
  for (const extra of options.files ?? []) {
    body.append("files", extra);
  }
  return send<{ analysis_id: string; status: string }>("/api/analyses", {
    method: "POST",
    body,
  });
}

export function listAnalyses() {
  return send<{ analyses: AnalysisListItem[] }>("/api/analyses");
}

export function getAnalysis(id: string) {
  return send<Analysis>(`/api/analyses/${id}`);
}

export function getRequirements(id: string) {
  return send<{ requirements: Requirement[] }>(`/api/analyses/${id}/requirements`);
}

export function getPages(id: string) {
  return send<{ pages: PagePreview[] }>(`/api/analyses/${id}/pages`);
}

export function addEvidence(id: string, files: File[]) {
  const body = new FormData();
  for (const file of files) {
    body.append("files", file);
  }
  return send<{ files: StoredFile[]; processing_status: string }>(
    `/api/analyses/${id}/evidence-files`,
    { method: "POST", body },
  );
}

export function matchEvidence(id: string) {
  return send<{ job_id: string; status: string }>(`/api/analyses/${id}/match-evidence`, {
    method: "POST",
  });
}

export function retryAnalysis(id: string) {
  return send<{ analysis_id: string; status: string }>(`/api/analyses/${id}/retry`, {
    method: "POST",
  });
}

export function cancelAnalysis(id: string) {
  return send<{ analysis_id: string; status: string }>(`/api/analyses/${id}/cancel`, {
    method: "POST",
  });
}

export function patchRequirement(
  id: string,
  decision: string,
  statement?: string,
  note?: string,
) {
  return send<Requirement>(`/api/requirements/${id}`, {
    method: "PATCH",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      decision,
      statement: statement ?? null,
      note: note ?? null,
    }),
  });
}

export async function downloadChecklist(id: string, language: string) {
  let response: Response;
  try {
    response = await fetch(`${API_BASE}/api/analyses/${id}/export.csv?lang=${encodeURIComponent(language)}`);
  } catch {
    throw new ApiError("api_unreachable", "The API did not respond.", true, 0);
  }
  if (!response.ok) {
    throw await readError(response);
  }
  const blob = await response.blob();
  const url = URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = url;
  link.download = "hr-checklist.csv";
  link.click();
  URL.revokeObjectURL(url);
}

export function deleteAnalysis(id: string) {
  return send<{ status: string }>(`/api/analyses/${id}`, { method: "DELETE" });
}

export function listDocuments() {
  return send<{ documents: LibraryDocument[] }>("/api/documents");
}

export function uploadDocuments(files: File[]) {
  const body = new FormData();
  for (const file of files) {
    body.append("files", file);
  }
  return send<{ saved: LibraryDocument[]; rejected: RejectedUpload[] }>("/api/documents", {
    method: "POST",
    body,
  });
}

export function deleteDocument(id: string) {
  return send<{ status: string }>(`/api/documents/${id}`, { method: "DELETE" });
}

export function documentUrl(id: string) {
  return `${API_BASE}/api/documents/${id}/content`;
}

export function analysisFileUrl(fileId: string, page?: number | null) {
  return `${API_BASE}/api/files/${fileId}/content${page ? `#page=${page}` : ""}`;
}

export async function fetchDocumentFile(document: LibraryDocument) {
  let response: Response;
  try {
    response = await fetch(documentUrl(document.document_id));
  } catch {
    throw new ApiError("api_unreachable", "The API did not respond.", true, 0);
  }
  if (!response.ok) {
    throw await readError(response);
  }
  const blob = await response.blob();
  return new File([blob], document.original_name, { type: "application/pdf" });
}

/** Fictional sample pack (generated, not real company data). The tender is always sent with sample=true. */
export const DEMO_TENDER = "/demo/uydurma-tender.pdf";

/** The API accepts at most 8 company files per analysis. */
export const DEMO_EVIDENCE = [
  "vergi-shehadetname.pdf",
  "tecrube-muqavile.pdf",
  "kataloq.pdf",
  "elaqesiz.pdf",
  "keyfiyyet-shehadetname.pdf",
  "qeyri-muayyen-tarix.pdf",
  "skan-bos.pdf",
  "vergi-shehadetname-surati.pdf",
];

export async function filesFromDemo(paths: string[]) {
  const files: File[] = [];
  for (const path of paths) {
    const response = await fetch(path);
    if (!response.ok) {
      throw new ApiError("generic", path, false, response.status);
    }
    const blob = await response.blob();
    const name = path.split("/").pop() ?? "document.pdf";
    files.push(new File([blob], name, { type: "application/pdf" }));
  }
  return files;
}
