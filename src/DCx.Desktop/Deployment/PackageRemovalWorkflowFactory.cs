using DCx.Core.Reliability;
using Windows.ApplicationModel;

namespace DCx.Desktop.Deployment;

internal static class PackageRemovalWorkflowFactory
{
    public static IPackageRemovalWorkflow CreateDefault()
    {
        try
        {
            var identity = Package.Current.Id;
            var review = PackageRemovalReviewPolicy.Create(
                identity.Name,
                identity.FullName,
                identity.Publisher,
                identity.PublisherId,
                FormattableString.Invariant(
                    $"{identity.Version.Major}.{identity.Version.Minor}.{identity.Version.Build}.{identity.Version.Revision}"),
                identity.Architecture.ToString(),
                DateTimeOffset.UtcNow);
            PackageRemovalReviewPolicy.Require(review);

            // A production package identity and signer policy have not been approved. Expose
            // the exact identity for review but do not compose the removal executor.
            return new UnavailablePackageRemovalWorkflow(
                PackageRemovalAvailability.ProductionIdentityNotConfigured,
                review);
        }
        catch
        {
            return new UnavailablePackageRemovalWorkflow(
                PackageRemovalAvailability.NotPackaged);
        }
    }

    public static IPackageRemovalWorkflow CreateValidationFixture() =>
        new ValidationPackageRemovalWorkflow(PackageRemovalReviewPolicy.Create(
            "DCx.AgenticBrowser.ValidationFixture",
            "DCx.AgenticBrowser.ValidationFixture_1.2.3.4_x64__fixture123456",
            "CN=DCx Synthetic Validation Fixture",
            "fixture123456",
            "1.2.3.4",
            "X64",
            DateTimeOffset.UtcNow));
}
