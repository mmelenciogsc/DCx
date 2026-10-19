using System.Net;
using System.Security.Cryptography;

namespace DCx.Core.GlobalKnowledge;

public sealed record GlobalKnowledgeSourceConfiguration(
    IGlobalKnowledgeViewSource Source,
    bool IsEnrolled);

public static class GlobalKnowledgeSourceFactory
{
    public static Task<GlobalKnowledgeSourceConfiguration> CreateAsync(
        string applicationDataRoot,
        IGlobalRegistryEnrollmentStore enrollmentStore,
        CancellationToken cancellationToken = default) =>
        CreateCoreAsync(
            applicationDataRoot,
            enrollmentStore,
            trustPolicyAnchorStore: null,
            CreateHardenedHandler,
            cancellationToken);

    internal static async Task<GlobalKnowledgeSourceConfiguration> CreateCoreAsync(
        string applicationDataRoot,
        IGlobalRegistryEnrollmentStore enrollmentStore,
        IGlobalTrustPolicyAnchorStore? trustPolicyAnchorStore,
        Func<HttpMessageHandler> handlerFactory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDataRoot);
        ArgumentNullException.ThrowIfNull(enrollmentStore);
        ArgumentNullException.ThrowIfNull(handlerFactory);
        var enrollment = await enrollmentStore.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (enrollment is null)
        {
            return new GlobalKnowledgeSourceConfiguration(
                new UnavailableGlobalKnowledgeViewSource(),
                false);
        }

        var trustRoot = GlobalRegistryEnrollmentPolicy.ExportTrustRoot(enrollment);
        HttpClient? httpClient = null;
        try
        {
            var client = trustPolicyAnchorStore is null
                ? await GlobalReplicaClient.OpenAsync(
                        applicationDataRoot,
                        trustRoot,
                        cancellationToken)
                    .ConfigureAwait(false)
                : await GlobalReplicaClient.OpenAsync(
                        applicationDataRoot,
                        trustRoot,
                        trustPolicyAnchorStore,
                        cancellationToken)
                    .ConfigureAwait(false);
            httpClient = new HttpClient(handlerFactory(), disposeHandler: true)
            {
                Timeout = TimeSpan.FromSeconds(30),
            };
            var endpoint = new Uri(enrollment.RegistryBaseUri, UriKind.Absolute);
            var source = new GlobalReplicaViewSource(
                client,
                new HttpGlobalRegistryTransport(httpClient, endpoint),
                httpClient);
            httpClient = null;
            return new GlobalKnowledgeSourceConfiguration(source, true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(trustRoot);
            httpClient?.Dispose();
        }
    }

    private static HttpMessageHandler CreateHardenedHandler() =>
        new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            UseProxy = false,
        };
}
