using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using TenderEvidenceChecker.Api.Tests.Support;
using Xunit.Abstractions;

namespace TenderEvidenceChecker.Api.Tests;

public class FixtureEvaluationTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    private readonly ITestOutputHelper _output;

    public FixtureEvaluationTests(ApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _output = output;
    }

    [Fact]
    public async Task Fictional_pack_is_grounded_and_cautious()
    {
        var root = DemoPack.Write();
        var tenderPath = Path.Combine(root, "tender", "uydurma-tender.pdf");
        var pages = DemoPack.ReadPages(tenderPath);
        Assert.Equal(9, pages.Count);
        foreach (var requirement in DemoPack.Requirements)
        {
            var page = pages[requirement.Page - 1];
            if (!page.Contains(requirement.Text, StringComparison.Ordinal))
            {
                var limit = Math.Min(requirement.Text.Length, page.Length);
                var index = 0;
                while (index < limit && requirement.Text[index] == page[index])
                {
                    index++;
                }

                var expectedCode = index < requirement.Text.Length ? ((int)requirement.Text[index]).ToString("X4") : "end";
                var actualCode = index < page.Length ? ((int)page[index]).ToString("X4") : "end";
                throw new Xunit.Sdk.XunitException(
                    $"Quote mismatch on page {requirement.Page} at {index}: expected U+{expectedCode} actual U+{actualCode}. Page text: {page}");
            }
        }

        var client = _factory.CreateClient();
        var created = await Upload(client, "/api/analyses", "file", "Şəhər kitabxanası mebeli", ("uydurma-tender.pdf", await File.ReadAllBytesAsync(tenderPath)));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var analysisId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("analysis_id").GetGuid();
        await _factory.DrainAsync();

        var requirementsResponse = await client.GetFromJsonAsync<JsonElement>($"/api/analyses/{analysisId}/requirements");
        var rows = requirementsResponse.GetProperty("requirements").EnumerateArray().ToList();
        var expected = DemoPack.Requirements;
        var hits = expected.Count(item => rows.Any(row =>
            row.GetProperty("page_number").GetInt32() == item.Page
            && row.GetProperty("quote_verified").GetBoolean()
            && (row.GetProperty("quote").GetString() ?? "").Contains(item.Text, StringComparison.Ordinal)
            && row.GetProperty("requirement_class").GetString() == item.RequirementClass));
        var quoteHits = rows.Count(row => row.GetProperty("quote_verified").GetBoolean());
        _output.WriteLine($"extraction_hits {hits}/{expected.Length}");
        _output.WriteLine($"predicted {rows.Count}");
        _output.WriteLine($"quote_verified {quoteHits}/{rows.Count}");
        Assert.Equal(expected.Length, hits);

        Assert.DoesNotContain(rows, row =>
            (row.GetProperty("quote").GetString() ?? "").Contains("Sistem təlimatı", StringComparison.Ordinal)
            && row.GetProperty("requirement_class").GetString() == "mandatory");

        using var evidence = new MultipartFormDataContent();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "supplier")))
        {
            var bytes = new ByteArrayContent(await File.ReadAllBytesAsync(file));
            bytes.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            evidence.Add(bytes, "files", Path.GetFileName(file));
        }

        var uploaded = await client.PostAsync($"/api/analyses/{analysisId}/evidence-files", evidence);
        if (uploaded.StatusCode != HttpStatusCode.OK)
        {
            throw new Xunit.Sdk.XunitException(await uploaded.Content.ReadAsStringAsync());
        }
        var uploadedBody = await uploaded.Content.ReadAsStringAsync();
        var uploadedJson = JsonSerializer.Deserialize<JsonElement>(uploadedBody);
        if (!uploadedJson.GetProperty("files").EnumerateArray().Any(file =>
                file.TryGetProperty("duplicate_of_file_id", out var duplicate) && duplicate.ValueKind != JsonValueKind.Null))
        {
            throw new Xunit.Sdk.XunitException(uploadedBody);
        }
        Assert.Contains(uploadedJson.GetProperty("files").EnumerateArray(), file =>
            file.GetProperty("original_name").GetString() == "skan-bos.pdf"
            && file.GetProperty("unreadable_pages").GetArrayLength() == 1);

        var match = await client.PostAsync($"/api/analyses/{analysisId}/match-evidence", new StringContent(""));
        Assert.Equal(HttpStatusCode.Accepted, match.StatusCode);
        await _factory.DrainAsync();

        var matched = (await client.GetFromJsonAsync<JsonElement>($"/api/analyses/{analysisId}/requirements"))
            .GetProperty("requirements").EnumerateArray().ToList();
        var experience = matched.Single(row => (row.GetProperty("quote").GetString() ?? "").Contains("təcrübəsini", StringComparison.Ordinal));
        var experienceFile = experience.GetProperty("evidence").EnumerateArray().Single().GetProperty("evidence_file_name").GetString();
        Assert.Equal("tecrube-muqavile.pdf", experienceFile);

        var quality = matched.Single(row => (row.GetProperty("quote").GetString() ?? "").Contains("keyfiyyət idarəetmə", StringComparison.Ordinal));
        var qualityMatch = quality.GetProperty("evidence").EnumerateArray().Single();
        Assert.Equal("expired_date_detected", qualityMatch.GetProperty("status").GetString());
        Assert.Contains("2020-05-01", qualityMatch.GetProperty("date_observation").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("eligible", qualityMatch.GetProperty("date_observation").GetString(), StringComparison.OrdinalIgnoreCase);

        var joint = matched.Single(row => (row.GetProperty("quote").GetString() ?? "").Contains("birgə fəaliyyət", StringComparison.Ordinal));
        var jointMatch = joint.GetProperty("evidence").EnumerateArray().Single();
        Assert.Equal("not_found", jointMatch.GetProperty("status").GetString());
        Assert.Contains("does not mean the person lacks", jointMatch.GetProperty("explanation").GetString(), StringComparison.Ordinal);

        Assert.DoesNotContain(matched, row => row.GetProperty("evidence").EnumerateArray().Any(match =>
            match.TryGetProperty("evidence_file_name", out var name)
            && name.GetString() == "qeyri-muayyen-tarix.pdf"
            && match.GetProperty("status").GetString() == "expired_date_detected"));

        var confirmed = experience.GetProperty("requirement_id").GetGuid();
        var patch = await client.PatchAsJsonAsync($"/api/requirements/{confirmed}", new { decision = "confirm", note = "Şəhadətnamə oxundu" });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        var formula = matched.Single(row => (row.GetProperty("quote").GetString() ?? "").Contains("20 bal", StringComparison.Ordinal));
        var edited = await client.PatchAsJsonAsync($"/api/requirements/{formula.GetProperty("requirement_id").GetGuid()}", new
        {
            decision = "edit",
            statement = "=HYPERLINK(\"http://example.test\")",
            note = "düzəliş"
        });
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);

        var reloaded = await client.GetFromJsonAsync<JsonElement>($"/api/analyses/{analysisId}/requirements");
        var confirmedRow = reloaded.GetProperty("requirements").EnumerateArray().Single(row => row.GetProperty("requirement_id").GetGuid() == confirmed);
        Assert.Equal("confirm", confirmedRow.GetProperty("reviewer_decision").GetString());
        Assert.Equal("Şəhadətnamə oxundu", confirmedRow.GetProperty("human_note").GetString());
        var editedRow = reloaded.GetProperty("requirements").EnumerateArray()
            .Single(row => row.GetProperty("requirement_id").GetGuid() == formula.GetProperty("requirement_id").GetGuid());
        Assert.Equal("=HYPERLINK(\"http://example.test\")", editedRow.GetProperty("edited_statement").GetString());
        Assert.NotEqual(editedRow.GetProperty("statement").GetString(), editedRow.GetProperty("edited_statement").GetString());

        var export = await client.GetAsync($"/api/analyses/{analysisId}/export.csv");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        var csv = await export.Content.ReadAsStringAsync();
        Assert.Contains("şəhadətnamə", csv, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("'=HYPERLINK", csv, StringComparison.Ordinal);
        Assert.Contains("AI-assisted draft / human-reviewed", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("compliant", csv, StringComparison.OrdinalIgnoreCase);

        var other = await Upload(client, "/api/analyses", "file", "Digər tender", ("other.pdf", SimplePdf.Create("Supplier must provide a separate registration certificate.")));
        var otherId = (await other.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("analysis_id").GetGuid();
        var removed = await client.DeleteAsync($"/api/analyses/{analysisId}");
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        var otherStillThere = await client.GetAsync($"/api/analyses/{otherId}");
        Assert.Equal(HttpStatusCode.OK, otherStillThere.StatusCode);
    }

    private static async Task<HttpResponseMessage> Upload(HttpClient client, string url, string field, string label, (string Name, byte[] Bytes) file)
    {
        using var content = new MultipartFormDataContent();
        if (field == "file")
        {
            content.Add(new StringContent(label), "label");
        }

        var bytes = new ByteArrayContent(file.Bytes);
        bytes.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        content.Add(bytes, field, file.Name);
        return await client.PostAsync(url, content);
    }
}
