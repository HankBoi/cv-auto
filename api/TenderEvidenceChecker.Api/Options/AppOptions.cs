namespace TenderEvidenceChecker.Api.Options;

public sealed class AppOptions
{
    public string DatabaseUrl { get; set; } = "";
    public string PrivateUploadDir { get; set; } = "";
    public int MaxUploadMb { get; set; } = 15;
    public int MaxTenderPages { get; set; } = 40;
    public int MaxEvidenceFiles { get; set; } = 8;
    public int MaxLibraryDocuments { get; set; } = 30;
    public bool OcrEnabled { get; set; }

    /// <summary>Hours before an analysis is purged automatically. Zero keeps analyses until the user deletes them.</summary>
    public int RetentionHours { get; set; }

    public string PromptVersion { get; set; } = "2026-10-09.2";
    public int ModelTimeoutSeconds { get; set; } = 60;
    public int ModelRetryDelayMs { get; set; } = 1500;
    public int MaxModelRequestChars { get; set; } = 400_000;
    public int JobStaleSeconds { get; set; } = 900;
    public DateOnly ReferenceDate { get; set; } = new(2026, 10, 9);

    public string GeminiApiKey { get; set; } = "";
    public string GeminiModelId { get; set; } = "gemini-3.5-flash-lite";

    /// <summary>Empty or "gemini" uses Gemini when a key is set. "ollama" uses a local Qwen model.</summary>
    public string ModelProvider { get; set; } = "";
    public string OllamaBaseUrl { get; set; } = "http://127.0.0.1:11434";
    public string OllamaModelId { get; set; } = "qwen2.5:7b-instruct";
    public int OllamaTimeoutSeconds { get; set; } = 300;

    public string PaymentsMode { get; set; } = "demo";
    public decimal DemoMonthlyPriceAzn { get; set; } = 19m;
    public int FreeAnalysisLimit { get; set; } = 3;
    public int DemoSubscriptionDays { get; set; } = 30;

    public long MaxUploadBytes => Math.Max(1, MaxUploadMb) * 1024L * 1024L;
}
