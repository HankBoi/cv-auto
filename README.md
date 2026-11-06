# statific

A local checklist for hiring documents. You upload one requirements PDF (a vacancy, an onboarding list, or a contract) and, optionally, CVs, contracts, and certificates. The tool extracts requirements and risks, shows the source file, page, and an exact verified quote for each finding, suggests possible supporting documents, and lets a person confirm, edit, reject, or comment before exporting a CSV checklist.

It does not decide who to hire, rank candidates, write contracts, contact people, or promise an outcome.

The interface is available in Azerbaijani (default), Russian, and English. The toggle is in the header. Findings can be written in any of the three languages. Source quotes stay in the language of the document.

## Run locally

Requirements: .NET SDK 10, Node.js, and pnpm.

```bash
cd web && pnpm install && cd ..
cp .env.example .env     # then add GEMINI_API_KEY to .env (never commit it)
./scripts/dev.sh         # on Windows: start-app.bat
```

- App: http://127.0.0.1:43123
- API, on the same machine as the app: http://127.0.0.1:43124
- Health through the app: http://127.0.0.1:43123/api/health

`scripts/dev.sh` starts the API, a worker process that polls the job table, and the Next.js dev server. The page calls `/api` on its own address, and Next forwards that to the API. Without the API process, upload fails. Without the worker, an analysis stays queued. Set `WEB_PORT` when you run the web app on another local port; the API then accepts that origin for CORS.

## Screens

Home, New analysis, Results, Human review (confirm, edit, reject, uncertain, not in uploaded files, not applicable, comment), My documents, Analysis history, Export (CSV), and Plan.

## AI

Extraction runs on the server through Google Gemini (`GEMINI_MODEL_ID`, default `gemini-3.5-flash-lite`). The key is read from `GEMINI_API_KEY` in the server environment or the uncommitted `.env` file. It never reaches the browser. Model output is parsed as structured JSON and re-validated on the server. Each quote is checked against the extracted page text. A finding whose quote or page fails the check is shown as "Source not verified".

A local Qwen model is optional: set `MODEL_PROVIDER=ollama` only when Ollama is already running. Leave that variable unset to stay on Gemini. No model output is invented as a fallback. Test code uses scripted providers only.

Without a configured model the app stays usable for uploads and review, and the New analysis page shows a setup message.

"Not found in uploaded files" means only that the uploaded files did not contain it. It never means the person lacks the document.

Fictional sample pack: **Try the sample pack** on the New analysis page uploads the invented PDF pack in this repo, runs the real model on it, and marks the result as sample mode. Those sample files are still the original fictional document set. Sample runs do not count toward the free allowance and are kept apart from live results. The sample pack is limited to eight extra files because the API accepts at most eight supporting documents per analysis.

## Free allowance and demo subscription

The first three successfully completed analyses in the workspace are free. Failed, incomplete, and API-error runs and retries do not count. After that, a demo monthly subscription is required.

The subscription is a simulation. `PAYMENTS_MODE=demo` and `DEMO_MONTHLY_PRICE_AZN=19` turn it on. The screen says "Demo price only — not a real price." The "Confirm demo payment" button stores a 30-day expiry in the database. No card data is collected, no payment provider is connected, and no real charge happens.

Usage and subscription state are stored server-side in the SQLite database, in a single local workspace without login. Real customers would need authentication and authorization, with one workspace per account. Without them, anyone who can reach the API shares the same allowance.

## Data handling

- Uploaded PDFs are checked for type, size, page count, and readability, stored under generated names outside the web root (`data/uploads`), and can be deleted from My documents or Analysis history.
- Document text is treated as untrusted. Full document text is not written to logs.
- CSV cells that start with a formula character are escaped.
- Use only synthetic or permitted public documents during the demo. Uploaded documents are sent to the model service for analysis.
- `OCR_ENABLED` defaults to false. Pages without readable text stay visible as unread and are listed in the result.

## Deploy on Railway

The repo root is not deployable by itself (Railpack cannot pick a language there). Create two services and set **Settings → Root Directory** to `api` for the first and `web` for the second. Each folder has its own `railway.json`.

1. **API**: root `api/`, uses `api/Dockerfile`. Add a volume mounted at `/data`. Set `GEMINI_API_KEY` (and optionally `GEMINI_MODEL_ID`). The API listens on Railway's `PORT`, and `EMBED_WORKER=1` (set in the Dockerfile) runs the job worker inside the same process, so no second service is needed.
2. **Web**: root `web/`, build `pnpm install --frozen-lockfile && pnpm build`, start `pnpm start`. Set `API_PROXY_TARGET` to the API service's private URL (for example `http://<api-service>.railway.internal:<PORT>`). Leave `NEXT_PUBLIC_API_BASE_URL` empty so the browser calls `/api` on the web service.

The demo subscription still processes no real payment. There is no login, so everyone who opens the public URL shares one workspace. Keep real confidential documents out of a public demo.

## Checks

```bash
dotnet test api/TenderEvidenceChecker.slnx
cd web && pnpm typecheck && pnpm lint && pnpm check:messages
```

## Configuration

Names are listed in `.env.example`. Do not commit values. `.env` is ignored by git.

## Demo corpus

`tests/fixtures/tender-demo/` holds the fictional tender, synthetic supplier PDFs, and versioned ground truth. The same PDFs are served from `web/public/demo/` so the sample pack can load them. Every organisation and identifier in that pack is invented.
