using System.Text.Json;
using System.Text.Json.Serialization;

namespace TenderEvidenceChecker.Api.Services;

public static class ModelOutputParser
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static ModelRequirementDocument ParseRequirements(string json) =>
        Parse<ModelRequirementDocument>(json);

    public static ModelEvidenceDocument ParseEvidence(string json) =>
        Parse<ModelEvidenceDocument>(json);

    public static string ExtractJson(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLine = trimmed.IndexOf('\n');
            var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (firstLine >= 0 && lastFence > firstLine)
            {
                trimmed = trimmed[(firstLine + 1)..lastFence].Trim();
            }
        }

        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            return trimmed[start..(end + 1)];
        }

        return trimmed;
    }

    private static T Parse<T>(string json)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<T>(ExtractJson(json), Options);
            if (parsed is null)
            {
                throw new AppException("model_output_invalid", "The model returned an empty response. You can retry.", true, 502);
            }

            return parsed;
        }
        catch (AppException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw new AppException(
                "model_output_invalid",
                "The model response did not match the expected checklist shape. You can retry. No partial checklist was saved.",
                true,
                502);
        }
    }
}
