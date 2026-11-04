using WinForge.Domain.Identity;
using WinForge.Domain.Inventory;

namespace WinForge.Application.Identity;

public sealed class SoftwareIdentityAssessmentService(SoftwareIdentityEngine engine)
{
    public SoftwareIdentityAssessment Analyze(PassportSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return engine.Analyze(snapshot.Components);
    }

    public string BuildSummary(SoftwareIdentityAssessment assessment)
    {
        ArgumentNullException.ThrowIfNull(assessment);

        if (assessment.Products.Count == 0)
        {
            return "No software identity groups were reconciled from the current inventory.";
        }

        var activeWarnings = assessment.Warnings.Count;
        var conflictCount = assessment.Conflicts.Count;
        var confidenceCount = assessment.Products.Count(product => product.Confidence == SoftwareIdentityConfidence.High);
        var productText = assessment.Products.Count == 1 ? "1 reconciled software product" : $"{assessment.Products.Count:N0} reconciled software products";
        var conflictText = conflictCount == 0 ? "0 conflicts" : $"{conflictCount:N0} conflict(s)";
        var warningText = activeWarnings == 0 ? "0 warnings" : $"{activeWarnings:N0} warning(s)";
        var confidenceText = confidenceCount == 0 ? "no high-confidence matches" : $"{confidenceCount:N0} high-confidence matches";

        return $"{productText}; {conflictText}; {warningText}; {confidenceText}.";
    }
}
