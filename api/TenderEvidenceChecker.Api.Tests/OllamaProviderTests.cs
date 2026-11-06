using System.Net;
using System.Text;
using System.Text.Json;
using TenderEvidenceChecker.Api.Options;
using TenderEvidenceChecker.Api.Services;

namespace TenderEvidenceChecker.Api.Tests;

public class OllamaProviderTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add((request, body));
            return await respond(request);
        }
    }

    private static OllamaModelProvider Provider(StubHandler handler)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new AppOptions
        {
            OllamaBaseUrl = "http://127.0.0.1:11434",
            OllamaModelId = "qwen2.5:7b-instruct",
            ModelRetryDelayMs = 0
        });
        return new OllamaModelProvider(new HttpClient(handler), options);
    }

    private static readonly SourcePage Page = new()
    {
        FileId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
        FileName = "vakansiya.pdf",
        PageNumber = 2,
        Text = "The candidate must provide a signed employment contract before the start date.",
        Usable = true
    };

    private const string ValidRequirements = """
        {"requirements":[{"kind":"requirement","category":"required_document","severity":"medium","statement":"Provide a contract","explanation":"A contract is requested.","possible_impact":"The file may be incomplete.","next_step":"Review the contract.","requirement_class":"mandatory","mandatory_label":"explicit","source_file_id":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","page_number":2,"quote":"The candidate must provide a signed employment contract","due_date_text":null,"evidence_type":null,"needs_human_review":true,"review_reason":"Check the page."}]}
        """;

    [Fact]
    public async Task Chat_response_is_parsed_and_sent_to_local_ollama()
    {
        var handler = new StubHandler(_ => Task.FromResult(Ok(ValidRequirements)));
        var result = await Provider(handler).ExtractRequirementsAsync([Page], "en", CancellationToken.None);

        var row = Assert.Single(result.Requirements);
        Assert.Equal("required_document", row.Category);
        Assert.Equal(2, row.PageNumber);

        var (request, body) = Assert.Single(handler.Calls);
        Assert.Equal("http://127.0.0.1:11434/api/chat", request.RequestUri!.AbsoluteUri);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("qwen2.5:7b-instruct", json.RootElement.GetProperty("model").GetString());
        Assert.Equal("json", json.RootElement.GetProperty("format").GetString());
        Assert.Equal(0, json.RootElement.GetProperty("options").GetProperty("temperature").GetInt32());
        var system = json.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
        Assert.Contains("untrusted", system, StringComparison.Ordinal);
        Assert.Contains("requirements", system, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_model_is_reported_without_a_partial_checklist()
    {
        var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("{\"error\":\"model not found\"}", Encoding.UTF8, "application/json")
        }));
        var error = await Assert.ThrowsAsync<AppException>(() => Provider(handler).ExtractRequirementsAsync([Page], "az", CancellationToken.None));
        Assert.Equal("model_rejected", error.Code);
        Assert.Contains("ollama pull qwen2.5:7b-instruct", error.UserMessage, StringComparison.Ordinal);
    }

    private static HttpResponseMessage Ok(string inner) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(new { message = new { role = "assistant", content = inner } }),
            Encoding.UTF8,
            "application/json")
    };
}
