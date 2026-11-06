using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TenderEvidenceChecker.Api.Data;
using TenderEvidenceChecker.Api.Options;

namespace TenderEvidenceChecker.Api.Services;

public sealed class AnalysisProcessor(
    IServiceScopeFactory scopes,
    IOptions<AppOptions> appOptions,
    ILogger<AnalysisProcessor> logger)
{
    private static readonly JsonSerializerOptions SnapshotOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public async Task<bool> TryProcessNext(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await ReclaimStale(db, cancellationToken);
        await DeleteExpired(db, cancellationToken);

        var job = await db.Jobs
            .OrderBy(item => item.CreatedAt)
            .FirstOrDefaultAsync(item => item.Status == "queued", cancellationToken);
        if (job is null)
        {
            return false;
        }

        var analysis = await db.Analyses.FirstAsync(item => item.Id == job.AnalysisId, cancellationToken);
        await db.Entry(analysis).Collection(item => item.Files).Query().Include(file => file.Pages).LoadAsync(cancellationToken);
        await db.Entry(analysis).Collection(item => item.Requirements).Query().Include(requirement => requirement.Matches).LoadAsync(cancellationToken);

        if (analysis.Status == "cancelled")
        {
            job.Status = "cancelled";
            job.FinishedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }

        job.Status = "processing";
        job.StartedAt = DateTime.UtcNow;
        job.Attempts += 1;
        analysis.Status = "processing";
        analysis.ErrorCode = null;
        analysis.ErrorMessage = null;
        analysis.ErrorRetryable = false;
        analysis.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            if (job.Kind == "match")
            {
                await Match(scope.ServiceProvider, db, analysis, cancellationToken);
            }
            else
            {
                await Extract(scope.ServiceProvider, db, analysis, cancellationToken);
            }

            job.Status = analysis.Status == "cancelled" ? "cancelled" : "succeeded";
            job.FinishedAt = DateTime.UtcNow;
            analysis.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);

            if (job.Status == "succeeded")
            {
                var waiting = await db.Jobs.AnyAsync(
                    item => item.AnalysisId == analysis.Id && item.Status == "queued" && item.Id != job.Id,
                    cancellationToken);
                if (!waiting)
                {
                    var quota = scope.ServiceProvider.GetRequiredService<QuotaService>();
                    await quota.TryCountAsync(db, analysis, cancellationToken);
                }
            }
        }
        catch (Exception ex)
        {
            var code = ex is AppException app ? app.Code : "pdf_unreadable";
            var message = ex is AppException known
                ? known.UserMessage
                : "Processing failed before a checklist was saved. You can retry. The upload is still stored with this analysis.";
            logger.LogWarning("Analysis {AnalysisId} job failed with {Code} ({ExceptionType})", analysis.Id, code, ex.GetType().Name);
            db.ChangeTracker.Clear();
            var failedJob = await db.Jobs.FirstAsync(item => item.Id == job.Id, cancellationToken);
            var failedAnalysis = await db.Analyses.FirstAsync(item => item.Id == analysis.Id, cancellationToken);
            failedJob.Status = "failed";
            failedJob.ErrorCode = code;
            failedJob.FinishedAt = DateTime.UtcNow;
            failedAnalysis.Status = "failed";
            failedAnalysis.ProgressStage = "failed";
            failedAnalysis.ErrorCode = code;
            failedAnalysis.ErrorMessage = message;
            failedAnalysis.ErrorRetryable = ex is AppException { Retryable: true };
            failedAnalysis.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }

        return true;
    }

    private async Task Extract(IServiceProvider services, AppDbContext db, AnalysisEntity analysis, CancellationToken cancellationToken)
    {
        analysis.ProgressStage = "extracting_text";
        analysis.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        var pdf = services.GetRequiredService<PdfTextService>();
        var store = services.GetRequiredService<FileStore>();
        var options = appOptions.Value;
        foreach (var file in analysis.Files.Where(file => file.Role == "tender"))
        {
            if (file.Pages.Count > 0)
            {
                continue;
            }

            var extracted = pdf.Extract(store.PathFor(analysis.Id, file.StoredName), options.OcrEnabled);
            file.PageCount = extracted.Count;
            file.Status = extracted.Any(page => page.Usable) ? "extracted" : "unreadable";
            foreach (var page in extracted)
            {
                var entity = new PageTextEntity
                {
                    Id = Guid.NewGuid(),
                    FileId = file.Id,
                    PageNumber = page.PageNumber,
                    Text = page.Text,
                    Usable = page.Usable,
                    ExtractionMethod = page.ExtractionMethod,
                    OcrStatus = page.OcrStatus
                };
                db.Pages.Add(entity);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        if (await IsCancelled(db, analysis.Id, cancellationToken))
        {
            analysis.Status = "cancelled";
            return;
        }

        var usable = analysis.Files
            .Where(file => file.Role == "tender")
            .SelectMany(file => file.Pages)
            .Any(page => page.Usable);
        if (!usable)
        {
            throw new AppException(
                options.OcrEnabled ? "ocr_unavailable" : "pdf_unreadable",
                options.OcrEnabled
                    ? "No readable text was found, and Azerbaijani OCR is not available in this demo. Unreadable pages were kept visible. You can upload a text PDF or retry."
                    : "No readable text was found. Scanned pages are not dropped, but OCR is turned off in this demo, so they were not treated as parsed. Upload a text PDF or retry.",
                retryable: false);
        }

        analysis.ProgressStage = "extracting_requirements";
        analysis.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        var provider = services.GetRequiredService<IModelProvider>();
        var pages = analysis.Files
            .Where(file => file.Role == "tender")
            .SelectMany(file => file.Pages.Select(page => new SourcePage
            {
                FileId = file.Id,
                FileName = file.OriginalName,
                PageNumber = page.PageNumber,
                Text = page.Text,
                Usable = page.Usable
            }))
            .ToList();

        var extractedRequirements = await provider.ExtractRequirementsAsync(pages, analysis.Language, cancellationToken);
        if (await IsCancelled(db, analysis.Id, cancellationToken))
        {
            analysis.Status = "cancelled";
            return;
        }

        var untouched = analysis.Requirements.Where(requirement => requirement.ReviewerDecision is null).ToList();
        db.Requirements.RemoveRange(untouched);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var savedCount = 0;
        foreach (var candidate in extractedRequirements.Requirements)
        {
            var saved = ValidateRequirement(candidate, analysis, seen);
            if (saved is not null)
            {
                savedCount++;
                saved.AnalysisId = analysis.Id;
                db.Requirements.Add(saved);
                db.Entry(saved).State = EntityState.Added;
            }
        }

        analysis.ProviderId = provider.ProviderId;
        analysis.ModelId = provider.ModelId;
        analysis.PromptVersion = options.PromptVersion;
        analysis.ErrorCode = null;
        analysis.ErrorMessage = null;

        // Company documents attached when the analysis was started are matched in the same run.
        var hasEvidence = analysis.Files.Any(file => file.Role == "evidence");
        if (hasEvidence && savedCount > 0)
        {
            var matchJob = new JobEntity
            {
                Id = Guid.NewGuid(),
                AnalysisId = analysis.Id,
                Kind = "match",
                Status = "queued",
                CreatedAt = DateTime.UtcNow
            };
            db.Jobs.Add(matchJob);
            db.Entry(matchJob).State = EntityState.Added;
            analysis.Status = "queued";
            analysis.ProgressStage = "matching_evidence";
            return;
        }

        analysis.Status = "needs_review";
        analysis.ProgressStage = "needs_review";
    }

    private async Task Match(IServiceProvider services, AppDbContext db, AnalysisEntity analysis, CancellationToken cancellationToken)
    {
        analysis.ProgressStage = "matching_evidence";
        analysis.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        var provider = services.GetRequiredService<IModelProvider>();
        var evidencePages = analysis.Files
            .Where(file => file.Role == "evidence")
            .SelectMany(file => file.Pages.Select(page => new SourcePage
            {
                FileId = file.Id,
                FileName = file.OriginalName,
                PageNumber = page.PageNumber,
                Text = page.Text,
                Usable = page.Usable
            }))
            .ToList();
        // Only verified tender requirements are matched. Risk findings and rows whose source could not be verified are not.
        var matchable = analysis.Requirements
            .Where(requirement => requirement.Kind == "requirement" && requirement.QuoteVerified)
            .ToList();
        var prompts = matchable.Select(requirement => new RequirementPrompt
        {
            RequirementId = requirement.Id,
            Statement = requirement.EditedStatement ?? requirement.Statement,
            RequirementClass = requirement.RequirementClass,
            Quote = requirement.Quote ?? ""
        }).ToList();

        // With no readable uploaded page there is nothing to compare, so the model is not called.
        var matched = prompts.Count == 0 || !evidencePages.Any(page => page.Usable)
            ? new ModelEvidenceDocument()
            : await provider.MatchEvidenceAsync(prompts, evidencePages, analysis.Language, cancellationToken);
        if (await IsCancelled(db, analysis.Id, cancellationToken))
        {
            analysis.Status = "cancelled";
            return;
        }

        var pageIndex = evidencePages
            .GroupBy(page => (page.FileId, page.PageNumber))
            .ToDictionary(group => group.Key, group => group.First());
        foreach (var requirement in matchable.Where(requirement => requirement.ReviewerDecision is null))
        {
            db.Matches.RemoveRange(requirement.Matches);
            requirement.Matches.Clear();
            var link = matched.Matches.FirstOrDefault(item =>
                Guid.TryParse(item.RequirementId, out var id) && id == requirement.Id)
                ?? new ModelEvidenceLink
                {
                    RequirementId = requirement.Id.ToString(),
                    Status = "not_found",
                    Explanation = "Not found in the uploaded files. This does not mean the person lacks the document.",
                    ReviewReason = "The matcher returned no row for this requirement.",
                    NeedsHumanReview = true
                };
            var entity = ValidateMatch(link, pageIndex);
            entity.RequirementId = requirement.Id;
            ApplyDateCheck(entity, pageIndex);
            db.Matches.Add(entity);
            db.Entry(entity).State = EntityState.Added;
            requirement.ReviewStatus = entity.Status switch
            {
                "possible_match" => "evidence_suggested",
                "not_found" => "missing",
                "expired_date_detected" => "expired",
                _ => "unclear"
            };
            requirement.NeedsHumanReview = true;
            if (!string.IsNullOrWhiteSpace(entity.ReviewReason))
            {
                requirement.ReviewReason = entity.ReviewReason;
            }
        }

        analysis.ProviderId = provider.ProviderId;
        analysis.ModelId = provider.ModelId;
        analysis.Status = "needs_review";
        analysis.ProgressStage = "needs_review";
    }

    private static RequirementEntity? ValidateRequirement(ModelRequirement candidate, AnalysisEntity analysis, HashSet<string> seen)
    {
        var kind = FindingVocabulary.Kinds.Contains(candidate.Kind) ? candidate.Kind.ToLowerInvariant() : "requirement";
        var category = FindingVocabulary.Categories.Contains(candidate.Category) ? candidate.Category.ToLowerInvariant() : "other";
        var severity = FindingVocabulary.Severities.Contains(candidate.Severity) ? candidate.Severity.ToLowerInvariant() : "uncertain";
        var requirementClass = CitationValidator.RequirementClasses.Contains(candidate.RequirementClass)
            ? candidate.RequirementClass.ToLowerInvariant()
            : "uncertain";
        var label = CitationValidator.MandatoryLabels.Contains(candidate.MandatoryLabel)
            ? candidate.MandatoryLabel.ToLowerInvariant()
            : "unclear";
        Guid? fileId = Guid.TryParse(candidate.SourceFileId, out var parsed) ? parsed : null;
        var file = analysis.Files.FirstOrDefault(item => item.Id == fileId && item.Role == "tender");
        var page = file?.Pages.FirstOrDefault(item => item.PageNumber == candidate.PageNumber);
        var quoteOk = page is not null && CitationValidator.QuoteSupported(page.Text, candidate.Quote);
        var statement = string.IsNullOrWhiteSpace(candidate.Statement) ? candidate.Quote : candidate.Statement.Trim();
        if (string.IsNullOrWhiteSpace(statement))
        {
            return null;
        }

        var key = TextNorm.Normalize(statement) + "|" + file?.Id + "|" + candidate.PageNumber;
        if (!seen.Add(key))
        {
            return null;
        }

        var reason = candidate.ReviewReason;
        if (!quoteOk)
        {
            reason = "The source page or exact quote could not be verified, so this row is not shown as a supported fact.";
            requirementClass = "uncertain";
            label = "unclear";
            severity = "uncertain";
        }

        return new RequirementEntity
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            Category = category,
            Severity = severity,
            Explanation = Clip(candidate.Explanation, 1500),
            PossibleImpact = Clip(candidate.PossibleImpact, 1000),
            NextStep = Clip(candidate.NextStep, 1000),
            Statement = statement,
            RequirementClass = requirementClass,
            MandatoryLabel = label,
            SourceFileId = file?.Id,
            PageNumber = page?.PageNumber,
            Quote = quoteOk ? TextNorm.Normalize(candidate.Quote) : candidate.Quote,
            QuoteVerified = quoteOk,
            DueDateText = candidate.DueDateText,
            EvidenceType = candidate.EvidenceType,
            NeedsHumanReview = true,
            ReviewReason = reason,
            ReviewStatus = quoteOk ? "not_reviewed" : "unclear",
            AiSnapshotJson = JsonSerializer.Serialize(candidate, SnapshotOptions)
        };
    }

    private static string Clip(string? value, int max)
    {
        var text = (value ?? "").Trim();
        return text.Length <= max ? text : text[..max].TrimEnd() + "…";
    }

    private static EvidenceMatchEntity ValidateMatch(
        ModelEvidenceLink link,
        Dictionary<(Guid FileId, int PageNumber), SourcePage> pages)
    {
        var status = link.Status?.ToLowerInvariant() ?? "unclear";
        if (status is not ("possible_match" or "possible_mismatch" or "not_found" or "unclear"))
        {
            status = "unclear";
        }

        if (status == "not_found")
        {
            return new EvidenceMatchEntity
            {
                Id = Guid.NewGuid(),
                Status = "not_found",
                QuoteVerified = false,
                NeedsHumanReview = true,
                Explanation = "Not found in the uploaded files. This does not mean the person lacks the document.",
                ReviewReason = link.ReviewReason
            };
        }

        var fileOk = Guid.TryParse(link.EvidenceFileId, out var fileId);
        SourcePage? page = null;
        if (fileOk && link.PageNumber is int number)
        {
            pages.TryGetValue((fileId, number), out page);
        }

        var quoteOk = page is not null && CitationValidator.QuoteSupported(page.Text, link.Quote);
        if (!quoteOk)
        {
            status = "unclear";
        }

        return new EvidenceMatchEntity
        {
            Id = Guid.NewGuid(),
            EvidenceFileId = page?.FileId,
            PageNumber = page?.PageNumber,
            Quote = quoteOk ? TextNorm.Normalize(link.Quote) : link.Quote,
            QuoteVerified = quoteOk,
            Status = status,
            NeedsHumanReview = true,
            Explanation = link.Explanation,
            ReviewReason = quoteOk
                ? link.ReviewReason
                : "The evidence file, page, or quote could not be verified against the uploaded documents."
        };
    }

    private void ApplyDateCheck(EvidenceMatchEntity match, Dictionary<(Guid FileId, int PageNumber), SourcePage> pages)
    {
        if (match.EvidenceFileId is not Guid fileId || match.PageNumber is not int pageNumber)
        {
            return;
        }

        if (!pages.TryGetValue((fileId, pageNumber), out var page))
        {
            return;
        }

        var finding = DateChecker.Inspect(page.Text, appOptions.Value.ReferenceDate);
        if (finding.Kind == "expired" && match.Status == "possible_match")
        {
            match.Status = "expired_date_detected";
            match.DateObservation = finding.Basis;
            match.NeedsHumanReview = true;
            match.ReviewReason = finding.Basis;
        }
        else if (finding.Kind == "ambiguous")
        {
            match.Status = "unclear";
            match.DateObservation = finding.Basis;
            match.NeedsHumanReview = true;
            match.ReviewReason = finding.Basis;
        }
        else if (finding.Kind == "dated")
        {
            match.DateObservation = finding.Basis;
        }
    }

    private async Task ReclaimStale(AppDbContext db, CancellationToken cancellationToken)
    {
        var cutoff = DateTime.UtcNow.AddSeconds(-Math.Max(60, appOptions.Value.JobStaleSeconds));
        var stale = await db.Jobs
            .Include(job => job.Analysis)
            .Where(job => job.Status == "processing" && job.StartedAt != null && job.StartedAt < cutoff)
            .ToListAsync(cancellationToken);
        foreach (var job in stale)
        {
            job.Status = "failed";
            job.ErrorCode = "model_timeout";
            job.FinishedAt = DateTime.UtcNow;
            if (job.Analysis is not null && job.Analysis.Status == "processing")
            {
                job.Analysis.Status = "failed";
                job.Analysis.ProgressStage = "failed";
                job.Analysis.ErrorCode = "model_timeout";
                job.Analysis.ErrorMessage = "Processing took too long and was stopped. You can retry. The upload is still stored.";
                job.Analysis.ErrorRetryable = true;
                job.Analysis.UpdatedAt = DateTime.UtcNow;
            }
        }

        if (stale.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task DeleteExpired(AppDbContext db, CancellationToken cancellationToken)
    {
        if (appOptions.Value.RetentionHours <= 0)
        {
            return;
        }

        var cutoff = DateTime.UtcNow.AddHours(-appOptions.Value.RetentionHours);
        var old = await db.Analyses.Where(item => item.CreatedAt < cutoff).Select(item => item.Id).ToListAsync(cancellationToken);
        if (old.Count == 0)
        {
            return;
        }

        using var cleanup = scopes.CreateScope();
        var store = cleanup.ServiceProvider.GetRequiredService<FileStore>();
        foreach (var id in old)
        {
            store.DeleteAnalysis(id);
        }

        await db.Analyses.Where(item => old.Contains(item.Id)).ExecuteDeleteAsync(cancellationToken);
    }

    private static async Task<bool> IsCancelled(AppDbContext db, Guid id, CancellationToken cancellationToken)
    {
        var status = await db.Analyses.AsNoTracking()
            .Where(item => item.Id == id)
            .Select(item => item.Status)
            .FirstAsync(cancellationToken);
        return status == "cancelled";
    }
}
