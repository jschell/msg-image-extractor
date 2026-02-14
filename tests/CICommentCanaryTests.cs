using Xunit;

namespace MsgImageExtractor.Tests;

/// <summary>
/// Intentionally failing test used to validate that the CI workflow correctly
/// parses TRX output and posts a structured failure comment on a PR.
///
/// USAGE
///   Normally excluded from CI by --filter "Category!=canary".
///   The canary job in ci.yml runs --filter "Category=canary", expects the step
///   to fail, and posts the result via the "Post canary result to PR" step.
///
/// ARCHIVING
///   Once the comment posting has been validated, move this file to
///   tests/archived/ so it is excluded from the project glob.
/// </summary>
public sealed class CICommentCanaryTests
{
    [Fact]
    [Trait("Category", "canary")]
    public void CanaryFailure_ExercisesCommentPosting()
    {
        Assert.Fail("Canary: this failure should appear in the PR comment.");
    }
}
