using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TenderEvidenceChecker.Api.Data;
using TenderEvidenceChecker.Api.Options;
using TenderEvidenceChecker.Api.Services;

namespace TenderEvidenceChecker.Api.Api;

/// <summary>
/// Endpoints for the single local demo workspace: configuration, company document library, file viewing,
/// free-analysis quota, and the simulated monthly subscription. There is no authentication in this MVP.
/// </summary>
public static class WorkspaceEndpoints
{
    public static void MapWorkspaceEndpoints(this WebApplication app)
    {
        app.MapGet("/api/config", GetConfig);
        app.MapGet("/api/quota", GetQuota);
        app.MapPost("/api/subscription/demo-activate", ActivateDemo);

        app.MapGet("/api/documents", ListDocuments);
        app.MapPost("/api/documents", UploadDocuments);
        app.MapDelete("/api/documents/{documentId:guid}", DeleteDocument);
        app.MapGet("/api/documents/{documentId:guid}/content", DocumentContent);
        app.MapGet("/api/files/{fileId:guid}/content", AnalysisFileContent);
    }

    private static IResult GetConfig(IModelProvider provider, IOptions<AppOptions> options)
    {
        var settings = options.Value;
        return Results.Ok(new
        {
            model_configured = provider.IsConfigured,
            provider_id = provider.IsConfigured ? provider.ProviderId : null,
            model_id = provider.IsConfigured ? provider.ModelId : null,
            payments_mode = settings.PaymentsMode,
            ocr_enabled = false,
            limits = new
            {
                max_upload_mb = settings.MaxUploadMb,
                max_pages = settings.MaxTenderPages,
                max_company_documents_per_analysis = settings.MaxEvidenceFiles,
                max_library_documents = settings.MaxLibraryDocuments
            }
        });
    }

    private static async Task<IResult> GetQuota(AppDbContext db, QuotaService quota, CancellationToken cancellationToken) =>
        Results.Ok(QuotaDto(await quota.GetAsync(db, cancellationToken)));

    private static async Task<IResult> ActivateDemo(AppDbContext db, QuotaService quota, CancellationToken cancellationToken)
    {
        try
        {
            return Results.Ok(QuotaDto(await quota.ActivateDemoAsync(db, cancellationToken)));
        }
        catch (AppException ex)
        {
            return Endpoints.Error(ex.Code, ex.UserMessage, ex.Retryable, ex.StatusCode);
        }
    }

    private static object QuotaDto(QuotaStatus status) => new
    {
        free_limit = status.FreeLimit,
        free_used = status.FreeUsed,
        free_remaining = status.FreeRemaining,
        subscription_active = status.SubscriptionActive,
        subscription_status = status.SubscriptionStatus,
        subscription_expires_at = status.SubscriptionExpiresAt,
        can_start = status.CanStart,
        payments_mode = status.PaymentsMode,
        demo_price_azn = status.DemoPriceAzn,
        price_is_demo = true,
        real_payment_processed = false
    };

    private static async Task<IResult> ListDocuments(AppDbContext db, CancellationToken cancellationToken)
    {
        var documents = await db.LibraryDocuments.AsNoTracking()
            .Where(item => item.WorkspaceId == Workspaces.LocalId)
            .OrderByDescending(item => item.CreatedAt)
            .ToListAsync(cancellationToken);
        return Results.Ok(new { documents = documents.Select(DocumentDto) });
    }

    private static async Task<IResult> UploadDocuments(
        HttpRequest request,
        AppDbContext db,
        DocumentUpload uploads,
        FileStore store,
        IOptions<AppOptions> options,
        CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType)
        {
            return Endpoints.Error("unsupported_file_type", "Upload one or more HR PDFs.", false, 400);
        }

        var form = await request.ReadFormAsync(cancellationToken);
        var incoming = form.Files.GetFiles("files");
        if (incoming.Count == 0)
        {
            incoming = form.Files.ToList();
        }

        if (incoming.Count == 0)
        {
            return Endpoints.Error("unsupported_file_type", "Choose at least one company PDF.", false, 400);
        }

        if (incoming.Count > 10)
        {
            return Endpoints.Error("too_many_files", "Upload at most 10 files at a time.", false, 400);
        }

        var existing = await db.LibraryDocuments
            .Where(item => item.WorkspaceId == Workspaces.LocalId)
            .ToListAsync(cancellationToken);
        var saved = new List<LibraryDocumentEntity>();
        var rejected = new List<object>();
        foreach (var file in incoming)
        {
            try
            {
                if (existing.Count + saved.Count >= Math.Max(1, options.Value.MaxLibraryDocuments))
                {
                    throw new AppException("too_many_files", $"My documents keeps at most {options.Value.MaxLibraryDocuments} files. Delete one first.", false, 400);
                }

                var document = await uploads.SaveLibraryAsync(Workspaces.LocalId, file, cancellationToken);
                if (existing.Concat(saved).Any(item => item.Sha256 == document.Sha256))
                {
                    store.DeleteLibraryFile(document.StoredName);
                    throw new AppException("duplicate_file", "This exact file is already in My documents.", false, 409);
                }

                saved.Add(document);
            }
            catch (AppException ex)
            {
                rejected.Add(new
                {
                    original_name = TextNorm.SafeFileName(file.FileName),
                    code = ex.Code,
                    user_message = ex.UserMessage
                });
            }
        }

        if (saved.Count > 0)
        {
            db.LibraryDocuments.AddRange(saved);
            await db.SaveChangesAsync(cancellationToken);
        }

        return Results.Ok(new { saved = saved.Select(DocumentDto), rejected });
    }

    private static async Task<IResult> DeleteDocument(
        Guid documentId,
        AppDbContext db,
        FileStore store,
        CancellationToken cancellationToken)
    {
        var document = await db.LibraryDocuments
            .FirstOrDefaultAsync(item => item.Id == documentId && item.WorkspaceId == Workspaces.LocalId, cancellationToken);
        if (document is null)
        {
            return Endpoints.Error("document_not_found", "That document does not exist, or it was deleted.", false, 404);
        }

        db.LibraryDocuments.Remove(document);
        await db.SaveChangesAsync(cancellationToken);
        store.DeleteLibraryFile(document.StoredName);
        return Results.Ok(new { status = "deleted" });
    }

    private static async Task<IResult> DocumentContent(
        Guid documentId,
        HttpContext context,
        AppDbContext db,
        FileStore store,
        CancellationToken cancellationToken)
    {
        var document = await db.LibraryDocuments.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == documentId && item.WorkspaceId == Workspaces.LocalId, cancellationToken);
        if (document is null)
        {
            return Endpoints.Error("document_not_found", "That document does not exist, or it was deleted.", false, 404);
        }

        return Pdf(context, store.LibraryPath(document.StoredName), document.OriginalName);
    }

    private static async Task<IResult> AnalysisFileContent(
        Guid fileId,
        HttpContext context,
        AppDbContext db,
        FileStore store,
        CancellationToken cancellationToken)
    {
        var file = await db.Files.AsNoTracking()
            .Include(item => item.Analysis)
            .FirstOrDefaultAsync(item => item.Id == fileId && item.Analysis!.WorkspaceId == Workspaces.LocalId, cancellationToken);
        if (file is null)
        {
            return Endpoints.Error("document_not_found", "That file does not exist, or it was deleted.", false, 404);
        }

        return Pdf(context, store.PathFor(file.AnalysisId, file.StoredName), file.OriginalName);
    }

    private static IResult Pdf(HttpContext context, string path, string name)
    {
        if (!File.Exists(path))
        {
            return Endpoints.Error("document_not_found", "The stored file is missing. Upload it again.", false, 404);
        }

        context.Response.Headers.ContentDisposition = $"inline; filename*=UTF-8''{Uri.EscapeDataString(name)}";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers.CacheControl = "private, no-store";
        return Results.File(path, "application/pdf", enableRangeProcessing: true);
    }

    private static object DocumentDto(LibraryDocumentEntity document) => new
    {
        document_id = document.Id,
        original_name = document.OriginalName,
        size_bytes = document.SizeBytes,
        page_count = document.PageCount,
        created_at = document.CreatedAt
    };
}
