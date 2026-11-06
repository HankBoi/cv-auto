using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TenderEvidenceChecker.Api.Options;

namespace TenderEvidenceChecker.Api.Services;

/// <summary>
/// Local Qwen through Ollama on this machine. Document text is sent only to that local service.
/// Model output is untrusted: it is parsed again here and validated by the processor.
/// </summary>
public sealed class OllamaModelProvider(HttpClient http, IOptions<AppOptions> options) : IModelProvider
{
    private const int MaxAttempts = 3;
    private const int ExtractionChunkChars = 6_000;

    public string ProviderId => "ollama";
    public string ModelId => options.Value.OllamaModelId;
    public bool IsConfigured => true;

    public async Task<ModelRequirementDocument> ExtractRequirementsAsync(
        IReadOnlyList<SourcePage> pages,
        string language,
        CancellationToken cancellationToken)
    {
        var system = ModelPrompts.ExtractionSystem(language) + "\n\n" + RequirementShape;
        var combined = new ModelRequirementDocument();
        foreach (var group in Chunk(pages, ExtractionChunkChars))
        {
            var payload = group.Select(page => new
            {
                source_file_id = page.FileId,
                file_name = page.FileName,
                page_number = page.PageNumber,
                text = page.Text
            });
            var user = "pages:\n" + JsonSerializer.Serialize(payload);
            var parsed = await CompleteAsync(system, user, ModelOutputParser.ParseRequirements, cancellationToken);
            combined.Requirements.AddRange(parsed.Requirements);
        }

        return combined;
    }

    public async Task<ModelEvidenceDocument> MatchEvidenceAsync(
        IReadOnlyList<RequirementPrompt> requirements,
        IReadOnlyList<SourcePage> evidencePages,
        string language,
        CancellationToken cancellationToken)
    {
        var system = ModelPrompts.MatchSystem(language) + "\n\n" + EvidenceShape;
        var user = "requirements:\n" + JsonSerializer.Serialize(requirements.Select(requirement => new
        {
            requirement_id = requirement.RequirementId,
            requirement_class = requirement.RequirementClass,
            statement = requirement.Statement,
            quote = requirement.Quote
        })) + "\nevidence_pages:\n" + JsonSerializer.Serialize(evidencePages.Where(page => page.Usable).Select(page => new
        {
            evidence_file_id = page.FileId,
            file_name = page.FileName,
            page_number = page.PageNumber,
            text = page.Text
        }));
        return await CompleteAsync(system, user, ModelOutputParser.ParseEvidence, cancellationToken);
    }

    private async Task<T> CompleteAsync<T>(
        string system,
        string user,
        Func<string, T> parse,
        CancellationToken cancellationToken)
    {
        if (user.Length + system.Length > options.Value.MaxModelRequestChars)
        {
            throw new AppException(
                "request_too_large",
                "The documents are too large to send to the model in one request. Use fewer or shorter documents.",
                retryable: false,
                statusCode: 413);
        }

        AppException? last = null;
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(options.Value.ModelRetryDelayMs * attempt, cancellationToken);
            }

            var prompt = last is { Code: "model_output_invalid" }
                ? user + "\n\nThe previous response was not valid JSON. Return only the JSON object, with no markdown."
                : user;
            try
            {
                var text = await SendAsync(system, prompt, cancellationToken);
                return parse(text);
            }
            catch (AppException ex) when (ex.Code is "model_output_invalid" or "model_timeout" && attempt < MaxAttempts - 1)
            {
                last = ex;
            }
        }

        throw last ?? new AppException("model_output_invalid", "The model response could not be used. You can retry.", true, 502);
    }

    private async Task<string> SendAsync(string system, string user, CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = options.Value.OllamaModelId,
            ["stream"] = false,
            ["format"] = "json",
            ["messages"] = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user }
            },
            ["options"] = new Dictionary<string, object?>
            {
                ["temperature"] = 0,
                ["num_predict"] = 4096,
                ["num_ctx"] = 8192
            }
        };

        var baseUrl = options.Value.OllamaBaseUrl.TrimEnd('/');
        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/api/chat");
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new AppException("model_timeout", "The local model timed out. You can retry. No partial checklist was saved.", true, 504);
        }
        catch (HttpRequestException)
        {
            throw new AppException(
                "model_timeout",
                "Ollama is not running. Start it on this machine, then retry.",
                true,
                502);
        }

        using (response)
        {
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new AppException(
                    "model_rejected",
                    $"Ollama does not have {options.Value.OllamaModelId}. Run: ollama pull {options.Value.OllamaModelId}",
                    false,
                    502);
            }

            if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout)
            {
                throw new AppException("model_timeout", "The local model returned an error. You can retry.", true, 502);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new AppException(
                    "model_rejected",
                    $"Ollama rejected the model name. Check OLLAMA_MODEL_ID, then run: ollama pull {options.Value.OllamaModelId}",
                    false,
                    502);
            }

            return ReadText(raw);
        }
    }

    private static string ReadText(string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (!root.TryGetProperty("message", out var message)
                || !message.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(content.GetString()))
            {
                throw new JsonException("empty");
            }

            return content.GetString()!;
        }
        catch (JsonException)
        {
            throw new AppException("model_output_invalid", "The local model response could not be read. You can retry.", true, 502);
        }
    }

    private const string RequirementShape = """
        JSON shape, and no other keys:
        {"requirements":[{"kind":"requirement","category":"required_document","severity":"medium","statement":"","explanation":"","possible_impact":"","next_step":"","requirement_class":"mandatory","mandatory_label":"explicit","source_file_id":"","page_number":1,"quote":"","due_date_text":null,"evidence_type":null,"needs_human_review":true,"review_reason":""}]}
        page_number is a number. quote is copied from the page.
        """;

    private const string EvidenceShape = """
        JSON shape, and no other keys:
        {"matches":[{"requirement_id":"","status":"not_found","evidence_file_id":null,"page_number":null,"quote":null,"explanation":"","date_observation":null,"needs_human_review":true,"review_reason":""}]}
        status is possible_match, possible_mismatch, not_found, or unclear. page_number is a number or null.
        """;

    private static List<List<SourcePage>> Chunk(IReadOnlyList<SourcePage> pages, int maxChars)
    {
        var groups = new List<List<SourcePage>>();
        var current = new List<SourcePage>();
        var size = 0;
        foreach (var page in pages.Where(page => page.Usable))
        {
            if (current.Count > 0 && size + page.Text.Length > maxChars)
            {
                groups.Add(current);
                current = [];
                size = 0;
            }

            current.Add(page);
            size += page.Text.Length;
        }

        if (current.Count > 0)
        {
            groups.Add(current);
        }

        return groups.Count == 0 ? [[]] : groups;
    }
}
