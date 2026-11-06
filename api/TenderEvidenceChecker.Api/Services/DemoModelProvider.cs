namespace TenderEvidenceChecker.Api.Services;

/// <summary>
/// Rule-based extractor for automated tests only. It is never registered by the application host,
/// so the live app cannot show its output. Quotes are copied from page text so citation checks
/// can pass or fail honestly.
/// </summary>
public sealed class DemoModelProvider : IModelProvider
{
    public string ProviderId => "demo-extractor";
    public string ModelId => "demo-rules-2026-10-09";
    public bool IsConfigured => true;

    public Task<ModelRequirementDocument> ExtractRequirementsAsync(
        IReadOnlyList<SourcePage> pages,
        string language,
        CancellationToken cancellationToken)
    {
        var rows = new List<ModelRequirement>();
        foreach (var page in pages.Where(page => page.Usable))
        {
            foreach (var paragraph in TextNorm.Paragraphs(page.Text))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsInjection(paragraph))
                {
                    rows.Add(new ModelRequirement
                    {
                        Statement = "Sənəddə təlimat kimi görünən mətn var. O, tələb kimi qəbul edilməyib.",
                        RequirementClass = "uncertain",
                        MandatoryLabel = "unclear",
                        SourceFileId = page.FileId.ToString(),
                        PageNumber = page.PageNumber,
                        Quote = paragraph,
                        NeedsHumanReview = true,
                        ReviewReason = "Instruction-like text was treated as document content and was not followed."
                    });
                    continue;
                }

                if (!LooksLikeRequirement(paragraph))
                {
                    continue;
                }

                rows.Add(Classify(paragraph, page));
            }
        }

        return Task.FromResult(new ModelRequirementDocument { Requirements = rows });
    }

    public Task<ModelEvidenceDocument> MatchEvidenceAsync(
        IReadOnlyList<RequirementPrompt> requirements,
        IReadOnlyList<SourcePage> evidencePages,
        string language,
        CancellationToken cancellationToken)
    {
        var links = new List<ModelEvidenceLink>();
        var usablePages = evidencePages.Where(page => page.Usable).ToList();
        foreach (var requirement in requirements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (requirement.RequirementClass.Equals("uncertain", StringComparison.OrdinalIgnoreCase))
            {
                links.Add(Unclear(requirement, "The requirement itself is uncertain, so no document is suggested as support."));
                continue;
            }

            var requirementTokens = new HashSet<string>(TextNorm.Tokens(requirement.Quote + " " + requirement.Statement));
            SourcePage? bestPage = null;
            string? bestSentence = null;
            var bestScore = 0;
            var bestWasNegation = false;

            foreach (var page in usablePages)
            {
                foreach (var sentence in TextNorm.Paragraphs(page.Text))
                {
                    var sentenceTokens = TextNorm.Tokens(sentence);
                    var score = sentenceTokens.Count(token => requirementTokens.Contains(token));
                    var negation = TextNorm.Fold(sentence).Contains("deyil", StringComparison.Ordinal);
                    if (negation)
                    {
                        score = Math.Max(0, score - 3);
                    }

                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestPage = page;
                        bestSentence = sentence;
                        bestWasNegation = negation;
                    }
                }
            }

            if (bestPage is null || bestSentence is null || bestScore < 2)
            {
                links.Add(new ModelEvidenceLink
                {
                    RequirementId = requirement.RequirementId.ToString(),
                    Status = "not_found",
                    NeedsHumanReview = true,
                    Explanation = "Not found in the uploaded files. This does not mean the person lacks the document.",
                    ReviewReason = "No uploaded page shared enough exact wording with this requirement."
                });
                continue;
            }

            if (bestWasNegation)
            {
                links.Add(new ModelEvidenceLink
                {
                    RequirementId = requirement.RequirementId.ToString(),
                    EvidenceFileId = bestPage.FileId.ToString(),
                    PageNumber = bestPage.PageNumber,
                    Quote = bestSentence,
                    Status = "unclear",
                    NeedsHumanReview = true,
                    Explanation = "The closest uploaded text is similar but says it is not that document.",
                    ReviewReason = "Similar wording included a negation. It was not accepted as support."
                });
                continue;
            }

            links.Add(new ModelEvidenceLink
            {
                RequirementId = requirement.RequirementId.ToString(),
                EvidenceFileId = bestPage.FileId.ToString(),
                PageNumber = bestPage.PageNumber,
                Quote = bestSentence,
                Status = "possible_match",
                NeedsHumanReview = true,
                Explanation = "Possible supporting document. A person still needs to confirm it.",
                ReviewReason = "Automatic wording overlap is only a suggestion."
            });
        }

        return Task.FromResult(new ModelEvidenceDocument { Matches = links });
    }

    private static ModelEvidenceLink Unclear(RequirementPrompt requirement, string reason) => new()
    {
        RequirementId = requirement.RequirementId.ToString(),
        Status = "unclear",
        NeedsHumanReview = true,
        Explanation = reason,
        ReviewReason = reason
    };

    private static bool IsInjection(string paragraph)
    {
        var folded = TextNorm.Fold(paragraph);
        return folded.Contains("sistem təlimatı", StringComparison.Ordinal)
            || folded.Contains("əvvəlki qaydaları", StringComparison.Ordinal)
            || folded.Contains("ignore previous", StringComparison.Ordinal)
            || folded.Contains("ignore all previous", StringComparison.Ordinal);
    }

    private static bool LooksLikeRequirement(string paragraph)
    {
        if (paragraph.Length < 40)
        {
            return false;
        }

        var folded = TextNorm.Fold(paragraph);
        string[] cues =
        [
            "təqdim", "məcburi", "qiymətləndirilir", "tələb", "müqavilə", "şəhadətnam",
            "ziddiyyət", "shall", "must", "required"
        ];
        return cues.Any(cue => folded.Contains(cue, StringComparison.Ordinal));
    }

    private static ModelRequirement Classify(string paragraph, SourcePage page)
    {
        var folded = TextNorm.Fold(paragraph);
        var requirementClass = "informational";
        var label = "explicit";
        var reason = "Extracted from the requirements page. A person needs to confirm it.";

        if (folded.Contains("ziddiyyət", StringComparison.Ordinal) || folded.Contains("oxunması çətin", StringComparison.Ordinal))
        {
            requirementClass = "uncertain";
            label = "unclear";
            reason = "The clause is ambiguous or contradictory and needs a specialist.";
        }
        else if (folded.Contains("qiymətləndirilir", StringComparison.Ordinal))
        {
            requirementClass = "evaluated";
            label = "explicit";
            reason = "The document describes a scored item, not a pass/fail document by itself.";
        }
        else if (folded.Contains("əgər", StringComparison.Ordinal) || folded.Contains("yalnız", StringComparison.Ordinal))
        {
            requirementClass = "mandatory";
            label = "conditional";
            reason = "The clause applies only in a stated condition.";
        }
        else if (folded.Contains("məcburi", StringComparison.Ordinal) || folded.Contains("təqdim etməlidir", StringComparison.Ordinal))
        {
            requirementClass = "mandatory";
            label = "explicit";
        }

        string? evidenceType = null;
        if (folded.Contains("şəhadətnam", StringComparison.Ordinal))
        {
            evidenceType = "certificate";
        }
        else if (folded.Contains("müqavilə", StringComparison.Ordinal))
        {
            evidenceType = "contract";
        }

        var category = requirementClass == "uncertain"
            ? "conflicting_unclear"
            : folded.Contains("cərimə", StringComparison.Ordinal) || folded.Contains("təminat", StringComparison.Ordinal)
                ? "contract_terms"
                : evidenceType is not null ? "required_document" : "other";

        return new ModelRequirement
        {
            Kind = "requirement",
            Category = category,
            Severity = "uncertain",
            Explanation = reason,
            PossibleImpact = "A person needs to decide whether this affects the bid.",
            NextStep = "Review the quoted page.",
            Statement = paragraph,
            RequirementClass = requirementClass,
            MandatoryLabel = label,
            SourceFileId = page.FileId.ToString(),
            PageNumber = page.PageNumber,
            Quote = paragraph,
            EvidenceType = evidenceType,
            NeedsHumanReview = true,
            ReviewReason = reason
        };
    }
}
