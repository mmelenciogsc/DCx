using System.Security.Cryptography;
using DCx.Core.Contributions;

namespace DCx.Core.Tests.Contributions;

public sealed class CcbSandboxEvidenceTests
{
    private static readonly string ArtifactSha256 = Digest("artifact");
    private static readonly string TestSuiteSha256 = Digest("tests");
    private static readonly string LauncherSha256 = Digest("launcher");
    private static readonly string BackendSha256 = Digest("bubblewrap");
    private static readonly string RuntimeSha256 = Digest("runtime");

    [Fact]
    public void PassingEvidence_IsDeterministicAndBoundToAllInputs()
    {
        var request = Request();

        var first = CcbSandboxEvidence.Create(
            request, LauncherSha256, BackendSha256, RuntimeSha256, "result"u8);
        var second = CcbSandboxEvidence.Create(
            request, LauncherSha256, BackendSha256, RuntimeSha256, "result"u8);

        Assert.True(first.Passed);
        Assert.Equal(first, second);
        CcbSandboxEvidence.VerifyBoundResult(request, first);
        Assert.Throws<ContributionPolicyException>(() =>
            CcbSandboxEvidence.VerifyBoundResult(
                request with { TestSuiteSha256 = Digest("changed-tests") },
                first));
        Assert.Throws<ContributionPolicyException>(() =>
            CcbSandboxEvidence.VerifyBoundResult(
                request with { WorkspaceSha256 = Digest("changed-workspace") },
                first));
        Assert.Throws<ContributionPolicyException>(() =>
            CcbSandboxEvidence.VerifyBoundResult(
                request,
                first with { LauncherSha256 = Digest("changed-launcher") }));
    }

    [Fact]
    public void FailureEvidence_IsBoundAndCannotMasqueradeAsPass()
    {
        var request = Request();
        var failure = CcbSandboxEvidence.Fail(
            request, LauncherSha256, BackendSha256, RuntimeSha256, "sandbox_timeout");

        Assert.False(failure.Passed);
        Assert.Null(failure.OutputSha256);
        CcbSandboxEvidence.VerifyBoundResult(request, failure);
        Assert.Throws<ContributionPolicyException>(() =>
            CcbSandboxEvidence.VerifyBoundResult(
                request,
                failure with { Passed = true }));
        Assert.Throws<ContributionPolicyException>(() =>
            CcbSandboxEvidence.VerifyBoundResult(
                request,
                failure with { EvidenceSha256 = Digest("forged") }));
    }

    [Fact]
    public void RequestAdmission_RejectsTraversalWrongRuntimeAndOversizedInput()
    {
        Assert.Throws<ArgumentException>(() => CcbSandboxEvidence.ValidateRequest(Request() with
        {
            Entrypoint = "../tool.sh",
        }));
        Assert.Throws<ArgumentException>(() => CcbSandboxEvidence.ValidateRequest(Request() with
        {
            Entrypoint = "tool.exe",
        }));
        Assert.Throws<ArgumentException>(() => CcbSandboxEvidence.ValidateRequest(Request() with
        {
            Input = new byte[CcbSandboxEvidence.MaximumInputBytes + 1],
        }));
    }

    [Fact]
    public async Task WorkspaceDigest_IsCanonicalAndContentSensitive()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dcx-ccb-workspace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "tools"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "z.txt"), "last");
            await File.WriteAllTextAsync(Path.Combine(root, "tools", "a.sh"), "first");
            var aDigest = Digest("first");
            var zDigest = Digest("last");
            var expected = Digest($"{aDigest}  ./tools/a.sh\n{zDigest}  ./z.txt\n");

            var first = await CcbSandboxEvidence.ComputeWorkspaceSha256Async(root);
            await File.WriteAllTextAsync(Path.Combine(root, "z.txt"), "changed");
            var changed = await CcbSandboxEvidence.ComputeWorkspaceSha256Async(root);

            Assert.Equal(expected, first);
            Assert.NotEqual(first, changed);
            for (var index = 0; index < 257; index++)
            {
                Directory.CreateDirectory(Path.Combine(root, $"directory-{index:D3}"));
            }

            await Assert.ThrowsAsync<ContributionPolicyException>(() =>
                CcbSandboxEvidence.ComputeWorkspaceSha256Async(root));
        }
        finally
        {
            TestDirectory.DeleteIfExists(root);
        }
    }

    private static CcbSandboxExecutionRequest Request() => new(
        ArtifactSha256,
        Digest("workspace"),
        TestSuiteSha256,
        "/quarantine/package",
        "tools/main.sh",
        "fixture"u8.ToArray());

    private static string Digest(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
}
