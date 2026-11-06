using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TenderEvidenceChecker.Api.Data;
using TenderEvidenceChecker.Api.Services;

namespace TenderEvidenceChecker.Api.Api;

public static class Endpoints
{
    private static readonly HashSet<string> Decisions = new(StringComparer.OrdinalIgnoreCase)
    {
        "confirm", "edit", "reject", "uncertain", "not_applicable", "missing", "comment"
    };

    public static void MapAppEndpoints(this WebApplication app)
    {
        app.MapGet("/api/health", () => Results.Ok(new { status = "ok", service = "tender-evidence-checker" }));

        app.MapGet("/api/analyses", ListAnalyses);
        app.MapPost("/api/analyses", CreateAnalysis);
        app.MapGet("/api/analyses/{analysisId:guid}", GetAnalysis);
        app.MapGet("/api/analyses/{analysisId:guid}/requirements", GetRequirements);
        app.MapGet("/api/analyses/{analysisId:guid}/pages", GetPages);
        app.MapPost("/api/analyses/{analysisId:guid}/evidence-files", AddEvidence);
        app.MapPost("/api/analyses/{analysisId:guid}/match-evidence", MatchEvidence);
        app.MapPost("/api/analyses/{analysisId:guid}/retry", Retry);
        app.MapPost("/api/analyses/{analysisId:guid}/cancel", Cancel);
        app.MapPatch("/api/requirements/{requirementId:guid}", PatchRequirement);
        app.MapGet("/api/analyses/{analysisId:guid}/export.csv", Export);
        app.MapDelete("/api/analyses/{analysisId:guid}", DeleteAnalysis);

        app.MapWorkspaceEndpoints();
    }

    private static async Task<IResult> ListAnalyses(AppDbContext db, CancellationToken cancellationToken)
    {
        var rows = await db.Analyses.AsNoTracking()
            .Where(item => item.WorkspaceId == Workspaces.LocalId)
            .OrderByDescending(item => item.CreatedAt)
            .Select(item => new
            {
                item.Id,
                item.Label,
                item.Status,
                item.ProgressStage,
                item.CreatedAt,
                item.UpdatedAt,
                item.IsSample,
                item.Language,
                item.ErrorCode,
                item.ModelId,
                Files = item.Files.Count,
                Findings = item.Requirements.Count,
                Reviewed = item.Requirements.Count(requirement => requirement.ReviewerDecision != null),
                NotVerified = item.Requirements.Count(requirement => !requirement.QuoteVerified)
            })
            .ToListAsync(cancellationToken);
        return Results.Ok(new
        {
            analyses = rows.Select(item => new
            {
                analysis_id = item.Id,
                label = item.Label,
                status = item.Status,
                progress_stage = item.ProgressStage,
                created_at = item.CreatedAt,
                updated_at = item.UpdatedAt,
                is_sample = item.IsSample,
                language = item.Language,
                error_code = item.ErrorCode,
                model_id = item.ModelId,
                files = item.Files,
                findings = item.Findings,
                reviewed = item.Reviewed,
                source_not_verified = item.NotVerified
            })
        });
    }

    private static async Task<IResult> CreateAnalysis(
        HttpRequest request,
        AppDbContext db,
        DocumentUpload uploads,
        FileStore store,
        IModelProvider provider,
        QuotaService quota,
        CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType)
        {
            return Error("unsupported_file_type", "Upload a PDF with the form field named file.", false, 400);
        }

        var form = await request.ReadFormAsync(cancellationToken);
        var label = (form["label"].ToString() ?? "").Trim();
        if (label.Length is < 2 or > 200)
        {
            return Error("unsupported_file_type", "Name the analysis with 2 to 200 characters, then choose a PDF.", false, 400);
        }

        var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault(item => item.Name != "files");
        if (file is null)
        {
            return Error("unsupported_file_type", "Choose one requirements PDF.", false, 400);
        }

        var isSample = string.Equals(form["sample"].ToString(), "true", StringComparison.OrdinalIgnoreCase);
        if (!provider.IsConfigured)
        {
            return Error("model_not_configured", UnconfiguredModelProvider.SetupMessage, false, 503);
        }

        if (!isSample)
        {
            var status = await quota.GetAsync(db, cancellationToken);
            if (!status.CanStart)
            {
                return Error(
                    "plan_required",
                    "The free analyses are used. Activate the monthly demo plan to start another analysis.",
                    false,
                    402);
            }
        }

        var documentIds = new List<Guid>();
        foreach (var raw in form["document_ids"])
        {
            if (!Guid.TryParse(raw, out var parsed))
            {
                return Error("document_not_found", "One of the selected company documents was not found.", false, 404);
            }

            if (!documentIds.Contains(parsed))
            {
                documentIds.Add(parsed);
            }
        }

        var adHoc = form.Files.GetFiles("files");
        if (documentIds.Count + adHoc.Count > uploads.FileLimit)
        {
            return Error("too_many_files", $"This demo keeps at most {uploads.FileLimit} company documents on one analysis.", false, 400);
        }

        var library = documentIds.Count == 0
            ? []
            : await db.LibraryDocuments.AsNoTracking()
                .Where(item => item.WorkspaceId == Workspaces.LocalId && documentIds.Contains(item.Id))
                .ToListAsync(cancellationToken);
        if (library.Count != documentIds.Count)
        {
            return Error("document_not_found", "One of the selected company documents was not found. It may have been deleted.", false, 404);
        }

        var analysis = new AnalysisEntity
        {
            Id = Guid.NewGuid(),
            WorkspaceId = Workspaces.LocalId,
            Label = label,
            Language = FindingVocabulary.NormalizeLanguage(form["language"].ToString()),
            IsSample = isSample,
            Status = "queued",
            ProgressStage = "queued",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        var evidence = new List<StoredFileEntity>();
        StoredFileEntity stored;
        try
        {
            stored = await uploads.SaveAsync(analysis.Id, file, "tender", extractText: false, cancellationToken);
            foreach (var document in library)
            {
                var path = store.LibraryPath(document.StoredName);
                if (!File.Exists(path))
                {
                    throw new AppException("document_not_found", "A selected company document is no longer stored. Upload it again.", false, 404);
                }

                var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
                evidence.Add(await uploads.SaveBytesAsync(analysis.Id, document.OriginalName, bytes, "evidence", extractText: true, cancellationToken));
            }

            foreach (var upload in adHoc)
            {
                evidence.Add(await uploads.SaveAsync(analysis.Id, upload, "evidence", extractText: true, cancellationToken));
            }
        }
        catch (AppException ex)
        {
            store.DeleteAnalysis(analysis.Id);
            return Error(ex.Code, ex.UserMessage, ex.Retryable, ex.StatusCode);
        }
        catch
        {
            store.DeleteAnalysis(analysis.Id);
            throw;
        }

        MarkDuplicates(evidence, []);
        analysis.Files.Add(stored);
        foreach (var item in evidence)
        {
            analysis.Files.Add(item);
        }

        analysis.Jobs.Add(new JobEntity
        {
            Id = Guid.NewGuid(),
            Kind = "extract",
            Status = "queued",
            CreatedAt = DateTime.UtcNow
        });
        db.Analyses.Add(analysis);
        await db.SaveChangesAsync(cancellationToken);

        return Results.Json(new
        {
            analysis_id = analysis.Id,
            status = analysis.Status,
            accepted_files = analysis.Files.Select(FileDto),
            warnings = Array.Empty<string>()
        }, statusCode: StatusCodes.Status201Created);
    }

    private static void MarkDuplicates(IReadOnlyList<StoredFileEntity> added, IReadOnlyList<StoredFileEntity> existing)
    {
        var known = existing.ToList();
        foreach (var file in added)
        {
            var earlier = known.FirstOrDefault(other => other.Sha256 == file.Sha256 && other.DuplicateOfFileId is null)
                ?? known.FirstOrDefault(other => other.Sha256 == file.Sha256);
            if (earlier is not null)
            {
                file.DuplicateOfFileId = earlier.Id;
            }
            else
            {
                known.Add(file);
            }
        }
    }

    private static async Task<IResult> GetAnalysis(Guid analysisId, AppDbContext db, CancellationToken cancellationToken)
    {
        var analysis = await Load(db, analysisId, cancellationToken);
        if (analysis is null)
        {
            return Missing();
        }

        var pages = analysis.Files.SelectMany(file => file.Pages).ToList();
        var stale = analysis.Status is "queued" or "processing"
            && analysis.UpdatedAt < DateTime.UtcNow.AddSeconds(-90);
        return Results.Ok(new
        {
            analysis_id = analysis.Id,
            label = analysis.Label,
            language = analysis.Language,
            is_sample = analysis.IsSample,
            quota_counted = analysis.QuotaCounted,
            created_at = analysis.CreatedAt,
            updated_at = analysis.UpdatedAt,
            status = analysis.Status,
            progress_stage = analysis.ProgressStage,
            provider_id = analysis.ProviderId,
            model_id = analysis.ModelId,
            prompt_version = analysis.PromptVersion,
            stale,
            counts = new
            {
                files = analysis.Files.Count,
                pages = pages.Count,
                usable_pages = pages.Count(page => page.Usable),
                unreadable_pages = pages.Count(page => !page.Usable),
                requirements = analysis.Requirements.Count
            },
            summary = Summary(analysis),
            error = analysis.ErrorCode is null
                ? null
                : new
                {
                    code = analysis.ErrorCode,
                    user_message = analysis.ErrorMessage,
                    retryable = analysis.ErrorRetryable
                },
            warnings = Warnings(analysis),
            files = analysis.Files.Select(FileDto)
        });
    }

    /// <summary>Counts only. There is intentionally no overall risk score and no outcome probability.</summary>
    private static object Summary(AnalysisEntity analysis)
    {
        var rows = analysis.Requirements;
        var verified = rows.Where(item => item.QuoteVerified).ToList();
        var matches = rows.SelectMany(item => item.Matches).ToList();
        int Severity(string value) => verified.Count(item => item.Severity == value);
        int Category(string value) => verified.Count(item => item.Category == value);
        return new
        {
            findings_total = rows.Count,
            source_verified = verified.Count,
            source_not_verified = rows.Count - verified.Count,
            requirements = rows.Count(item => item.Kind == "requirement"),
            risks = rows.Count(item => item.Kind == "risk"),
            reviewed = rows.Count(item => item.ReviewerDecision is not null),
            by_severity = new
            {
                high = Severity("high"),
                medium = Severity("medium"),
                low = Severity("low"),
                uncertain = Severity("uncertain")
            },
            by_category = new
            {
                required_document = Category("required_document"),
                deadline = Category("deadline"),
                technical_mismatch = Category("technical_mismatch"),
                contract_terms = Category("contract_terms"),
                conflicting_unclear = Category("conflicting_unclear"),
                other = Category("other")
            },
            evidence = new
            {
                possible_match = matches.Count(item => item.Status == "possible_match"),
                possible_mismatch = matches.Count(item => item.Status == "possible_mismatch"),
                not_found_in_uploaded_files = matches.Count(item => item.Status == "not_found"),
                expiry_date_seen = matches.Count(item => item.Status == "expired_date_detected"),
                unclear = matches.Count(item => item.Status == "unclear")
            }
        };
    }

    private static async Task<IResult> GetRequirements(Guid analysisId, AppDbContext db, CancellationToken cancellationToken)
    {
        var analysis = await Load(db, analysisId, cancellationToken);
        if (analysis is null)
        {
            return Missing();
        }

        var files = analysis.Files.ToDictionary(file => file.Id);
        return Results.Ok(new
        {
            requirements = analysis.Requirements
                .OrderBy(item => item.PageNumber ?? int.MaxValue)
                .ThenBy(item => item.Statement)
                .Select(item => RequirementDto(item, files))
        });
    }

    private static async Task<IResult> GetPages(Guid analysisId, AppDbContext db, CancellationToken cancellationToken)
    {
        var analysis = await Load(db, analysisId, cancellationToken);
        if (analysis is null)
        {
            return Missing();
        }

        var pages = analysis.Files
            .OrderBy(file => file.Role)
            .ThenBy(file => file.OriginalName)
            .SelectMany(file => file.Pages.OrderBy(page => page.PageNumber).Select(page => new
            {
                file_id = file.Id,
                file_name = file.OriginalName,
                role = file.Role,
                page_number = page.PageNumber,
                usable = page.Usable,
                extraction_method = page.ExtractionMethod,
                ocr_status = page.OcrStatus,
                preview = page.Usable ? TextNorm.Preview(page.Text, 320) : ""
            }));
        return Results.Ok(new { pages });
    }

    private static async Task<IResult> AddEvidence(
        Guid analysisId,
        HttpRequest request,
        AppDbContext db,
        DocumentUpload uploads,
        CancellationToken cancellationToken)
    {
        var analysis = await Load(db, analysisId, cancellationToken);
        if (analysis is null)
        {
            return Missing();
        }

        if (analysis.Status is "queued" or "processing")
        {
            return Error("export_failed", "Wait until the checklist finishes before adding HR documents.", true, 409);
        }

        if (!request.HasFormContentType)
        {
            return Error("unsupported_file_type", "Upload one or more HR PDFs.", false, 400);
        }

        var form = await request.ReadFormAsync(cancellationToken);
        var incoming = form.Files.GetFiles("files");
        if (incoming.Count == 0)
        {
            incoming = form.Files.ToList();
        }

        if (incoming.Count == 0)
        {
            return Error("unsupported_file_type", "Choose at least one company PDF.", false, 400);
        }

        var existing = analysis.Files.Count(file => file.Role == "evidence");
        var limit = uploads.FileLimit;
        if (existing + incoming.Count > limit)
        {
            return Error("too_many_files", $"This demo keeps at most {limit} company documents on one analysis.", false, 400);
        }

        var saved = new List<StoredFileEntity>();
        try
        {
            foreach (var file in incoming)
            {
                var stored = await uploads.SaveAsync(analysis.Id, file, "evidence", extractText: true, cancellationToken);
                analysis.Files.Add(stored);
                db.Entry(stored).State = EntityState.Added;
                foreach (var page in stored.Pages)
                {
                    db.Entry(page).State = EntityState.Added;
                }

                saved.Add(stored);
            }

            var known = analysis.Files.Where(file => saved.All(item => item.Id != file.Id)).ToList();
            MarkDuplicates(saved, known);

            analysis.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (AppException ex)
        {
            return Error(ex.Code, ex.UserMessage, ex.Retryable, ex.StatusCode);
        }

        return Results.Ok(new
        {
            files = saved.Select(FileDto),
            processing_status = "stored"
        });
    }

    private static async Task<IResult> MatchEvidence(Guid analysisId, AppDbContext db, IModelProvider provider, CancellationToken cancellationToken)
    {
        var analysis = await Load(db, analysisId, cancellationToken);
        if (analysis is null)
        {
            return Missing();
        }

        if (!provider.IsConfigured)
        {
            return Error("model_not_configured", UnconfiguredModelProvider.SetupMessage, false, 503);
        }

        if (analysis.Requirements.Count == 0 || analysis.Status is "queued" or "processing" or "failed")
        {
            return Error("export_failed", "Extract a checklist before asking for evidence suggestions.", true, 409);
        }

        if (analysis.Requirements.Any(requirement => requirement.ReviewerDecision is not null))
        {
            return Error("export_failed", "Review decisions are already saved. They were not overwritten.", false, 409);
        }

        if (analysis.Jobs.Any(job => job.Kind == "match" && job.Status is "queued" or "processing"))
        {
            return Results.Ok(new { job_id = analysis.Jobs.Last(job => job.Kind == "match").Id, status = "queued" });
        }

        var job = new JobEntity
        {
            Id = Guid.NewGuid(),
            Kind = "match",
            Status = "queued",
            CreatedAt = DateTime.UtcNow
        };
        analysis.Jobs.Add(job);
        analysis.Status = "queued";
        analysis.ProgressStage = "matching_evidence";
        analysis.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Accepted($"/api/analyses/{analysis.Id}", new { job_id = job.Id, status = "queued" });
    }

    private static async Task<IResult> Retry(Guid analysisId, AppDbContext db, IModelProvider provider, CancellationToken cancellationToken)
    {
        var analysis = await Load(db, analysisId, cancellationToken);
        if (analysis is null)
        {
            return Missing();
        }

        if (analysis.Status != "failed")
        {
            return Error("export_failed", "Retry is available after a failed run. This analysis is not failed.", false, 409);
        }

        if (!provider.IsConfigured)
        {
            return Error("model_not_configured", UnconfiguredModelProvider.SetupMessage, false, 503);
        }

        if (analysis.Jobs.Any(job => job.Status is "queued" or "processing"))
        {
            return Error("export_failed", "A job is already queued for this analysis.", true, 409);
        }

        var kind = analysis.Requirements.Count == 0 ? "extract" : "match";
        analysis.Jobs.Add(new JobEntity
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            Status = "queued",
            CreatedAt = DateTime.UtcNow
        });
        analysis.Status = "queued";
        analysis.ErrorCode = null;
        analysis.ErrorMessage = null;
        analysis.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(new { analysis_id = analysis.Id, status = "queued" });
    }

    private static async Task<IResult> Cancel(Guid analysisId, AppDbContext db, CancellationToken cancellationToken)
    {
        var analysis = await db.Analyses.Include(item => item.Jobs)
            .FirstOrDefaultAsync(item => item.Id == analysisId && item.WorkspaceId == Workspaces.LocalId, cancellationToken);
        if (analysis is null)
        {
            return Missing();
        }

        if (analysis.Status is not ("queued" or "processing"))
        {
            return Error("export_failed", "Only a queued or running analysis can be cancelled.", false, 409);
        }

        analysis.Status = "cancelled";
        analysis.ProgressStage = "cancelled";
        analysis.UpdatedAt = DateTime.UtcNow;
        foreach (var job in analysis.Jobs.Where(job => job.Status == "queued"))
        {
            job.Status = "cancelled";
            job.FinishedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(new { analysis_id = analysis.Id, status = "cancelled" });
    }

    private static async Task<IResult> PatchRequirement(
        Guid requirementId,
        ReviewPatch body,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var requirement = await db.Requirements
            .Include(item => item.Analysis)
            .FirstOrDefaultAsync(item => item.Id == requirementId && item.Analysis!.WorkspaceId == Workspaces.LocalId, cancellationToken);
        if (requirement?.Analysis is null)
        {
            return Missing();
        }

        var decision = (body.Decision ?? "").Trim().ToLowerInvariant();
        if (!Decisions.Contains(decision))
        {
            return Error("review_invalid", "Choose confirm, edit, reject, uncertain, missing, not applicable, or comment.", false, 400);
        }

        if (decision is "not_applicable" or "comment" && string.IsNullOrWhiteSpace(body.Note))
        {
            return Error("note_required", "This decision needs a short note.", false, 400);
        }

        if (decision == "edit" && string.IsNullOrWhiteSpace(body.Statement))
        {
            return Error("statement_required", "Write the corrected requirement before saving an edit.", false, 400);
        }

        if ((body.Note?.Length ?? 0) > 2000 || (body.Statement?.Length ?? 0) > 2000)
        {
            return Error("review_invalid", "Notes and edited statements are limited to 2000 characters.", false, 400);
        }

        if (string.IsNullOrWhiteSpace(requirement.AiSnapshotJson))
        {
            requirement.AiSnapshotJson = JsonSerializer.Serialize(new { statement = requirement.Statement, review_status = requirement.ReviewStatus });
        }

        if (decision == "comment")
        {
            // A comment keeps the AI suggestion and any earlier decision untouched.
            requirement.HumanNote = body.Note!.Trim();
            requirement.Analysis.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(RequirementDto(requirement, await FilesFor(db, requirement.AnalysisId, cancellationToken)));
        }

        requirement.ReviewerDecision = decision;
        requirement.HumanNote = string.IsNullOrWhiteSpace(body.Note) ? requirement.HumanNote : body.Note.Trim();
        requirement.ReviewedAt = DateTime.UtcNow;
        requirement.ReviewStatus = decision switch
        {
            "edit" => "changed",
            "confirm" => "confirmed",
            "not_applicable" => "not_applicable",
            "missing" => "missing",
            "reject" => "rejected",
            _ => "unclear"
        };
        if (decision == "edit")
        {
            requirement.EditedStatement = body.Statement!.Trim();
        }

        requirement.Analysis.UpdatedAt = DateTime.UtcNow;
        var siblings = await db.Requirements.Where(item => item.AnalysisId == requirement.AnalysisId).ToListAsync(cancellationToken);
        var allDecided = siblings.All(item => item.Id == requirement.Id || item.ReviewerDecision is not null);
        if (allDecided)
        {
            var unresolved = siblings.Any(item => item.ReviewStatus is "missing" or "expired" or "unclear" or "not_reviewed" or "evidence_suggested");
            requirement.Analysis.Status = unresolved ? "completed_with_unresolved_items" : "completed";
            requirement.Analysis.ProgressStage = requirement.Analysis.Status;
        }
        else
        {
            requirement.Analysis.Status = "needs_review";
        }

        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(RequirementDto(requirement, await FilesFor(db, requirement.AnalysisId, cancellationToken)));
    }

    private static async Task<Dictionary<Guid, StoredFileEntity>> FilesFor(AppDbContext db, Guid analysisId, CancellationToken cancellationToken)
    {
        var files = await db.Files.Include(file => file.Pages).Where(file => file.AnalysisId == analysisId).ToListAsync(cancellationToken);
        return files.ToDictionary(file => file.Id);
    }

    private static async Task<IResult> Export(Guid analysisId, HttpRequest request, AppDbContext db, CancellationToken cancellationToken)
    {
        var analysis = await Load(db, analysisId, cancellationToken);
        if (analysis is null)
        {
            return Missing();
        }

        if (analysis.Status is "queued" or "processing" or "failed" or "cancelled" || analysis.Requirements.Count == 0)
        {
            return Error(
                "export_failed",
                "Export is available after a checklist exists. A failed or unfinished analysis was not turned into an empty file.",
                analysis.ErrorRetryable,
                409);
        }

        var bytes = CsvExporter.Build(analysis, request.Query["lang"].ToString());
        return Results.File(bytes, "text/csv; charset=utf-8", "hr-checklist.csv");
    }

    private static async Task<IResult> DeleteAnalysis(
        Guid analysisId,
        AppDbContext db,
        FileStore store,
        CancellationToken cancellationToken)
    {
        var analysis = await db.Analyses
            .FirstOrDefaultAsync(item => item.Id == analysisId && item.WorkspaceId == Workspaces.LocalId, cancellationToken);
        if (analysis is null)
        {
            return Missing();
        }

        db.Analyses.Remove(analysis);
        await db.SaveChangesAsync(cancellationToken);
        store.DeleteAnalysis(analysisId);
        return Results.Ok(new { status = "deleted" });
    }

    internal static async Task<AnalysisEntity?> Load(AppDbContext db, Guid id, CancellationToken cancellationToken)
    {
        return await db.Analyses
            .Include(item => item.Files)
            .ThenInclude(file => file.Pages)
            .Include(item => item.Requirements)
            .ThenInclude(requirement => requirement.Matches)
            .Include(item => item.Jobs)
            .AsSplitQuery()
            .FirstOrDefaultAsync(item => item.Id == id && item.WorkspaceId == Workspaces.LocalId, cancellationToken);
    }

    internal static object FileDto(StoredFileEntity file) => new
    {
        file_id = file.Id,
        original_name = file.OriginalName,
        role = file.Role,
        size_bytes = file.SizeBytes,
        page_count = file.PageCount,
        sha256 = file.Sha256,
        status = file.Status,
        duplicate_of_file_id = file.DuplicateOfFileId,
        unreadable_pages = file.Pages.Where(page => !page.Usable).Select(page => page.PageNumber).OrderBy(number => number).ToArray()
    };

    private static object RequirementDto(RequirementEntity requirement, IReadOnlyDictionary<Guid, StoredFileEntity> files)
    {
        files.TryGetValue(requirement.SourceFileId ?? Guid.Empty, out var source);
        return new
        {
            requirement_id = requirement.Id,
            kind = requirement.Kind,
            category = requirement.Category,
            severity = requirement.QuoteVerified ? requirement.Severity : "uncertain",
            explanation = requirement.Explanation,
            possible_impact = requirement.PossibleImpact,
            next_step = requirement.NextStep,
            statement = requirement.Statement,
            edited_statement = requirement.EditedStatement,
            requirement_class = requirement.RequirementClass,
            mandatory_label = requirement.MandatoryLabel,
            due_date_text = requirement.DueDateText,
            evidence_type = requirement.EvidenceType,
            source_file_id = requirement.SourceFileId,
            source_file_name = source?.OriginalName,
            page_number = requirement.PageNumber,
            quote = requirement.Quote,
            quote_verified = requirement.QuoteVerified,
            needs_human_review = requirement.NeedsHumanReview,
            review_reason = requirement.ReviewReason,
            review_status = requirement.ReviewStatus,
            reviewer_decision = requirement.ReviewerDecision,
            human_note = requirement.HumanNote,
            reviewed_at = requirement.ReviewedAt,
            ai_suggestion = new
            {
                statement = requirement.Statement,
                review_status = requirement.ReviewerDecision is null ? requirement.ReviewStatus : "suggestion_preserved"
            },
            evidence = requirement.Matches.Select(match =>
            {
                files.TryGetValue(match.EvidenceFileId ?? Guid.Empty, out var evidence);
                return new
                {
                    evidence_file_id = match.EvidenceFileId,
                    evidence_file_name = evidence?.OriginalName,
                    page_number = match.PageNumber,
                    quote = match.Quote,
                    quote_verified = match.QuoteVerified,
                    explanation = match.Explanation,
                    date_observation = match.DateObservation,
                    status = match.Status,
                    needs_human_review = match.NeedsHumanReview,
                    review_reason = match.ReviewReason
                };
            })
        };
    }

    private static IReadOnlyList<string> Warnings(AnalysisEntity analysis)
    {
        var warnings = new List<string>();
        if (analysis.ProviderId == "demo-extractor")
        {
            warnings.Add("demo_extractor");
        }

        foreach (var file in analysis.Files)
        {
            foreach (var page in file.Pages.Where(page => !page.Usable))
            {
                warnings.Add($"unreadable:{file.OriginalName}:p{page.PageNumber}");
            }

            if (file.DuplicateOfFileId is not null)
            {
                warnings.Add($"duplicate:{file.OriginalName}");
            }
        }

        return warnings;
    }

    internal static IResult Missing() =>
        Error("analysis_not_found", "That analysis does not exist, or it was deleted.", false, 404);

    internal static IResult Error(string code, string message, bool retryable, int status) =>
        Results.Json(new
        {
            code,
            user_message = message,
            retryable,
            request_id = Guid.NewGuid().ToString("n")
        }, statusCode: status);
}

public sealed class ReviewPatch
{
    public string? Decision { get; set; }
    public string? Statement { get; set; }
    public string? Note { get; set; }
}
