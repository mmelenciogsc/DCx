using System.Text;
using System.Text.Json;
using DCx.Core.Control;

namespace DCx.Core.Tests.Control;

public sealed class BrowserBridgeMessageValidatorTests
{
    private static readonly Uri ApprovedUri = new("https://fixture.example:8443/proof");
    private static readonly Uri ApprovedSource = new("https://fixture.example:8443/bridge");
    private static readonly Guid TabId = Guid.Parse("51515151-5151-5151-5151-515151515151");
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 0, 0, 0, TimeSpan.Zero);
    private const string SessionId = "session-opaque-4eb8f7c2";

    [Fact]
    public void ExactOriginAndBoundEnvelopeAreAccepted()
    {
        var validator = CreateValidator();

        var accepted = validator.TryValidate(
            ApprovedSource,
            ToUtf8(CreateEnvelope("fixture-ready", new { ready = true })),
            out var message,
            out var rejectionCode);

        Assert.True(accepted);
        Assert.NotNull(message);
        Assert.Equal("fixture-ready", message.Type);
        Assert.True(message.Payload.GetProperty("ready").GetBoolean());
        Assert.Equal(string.Empty, rejectionCode);
    }

    [Theory]
    [InlineData("fixture-ready", "{\"ready\":true}")]
    [InlineData("user-input", "{\"event\":\"take-control-requested\"}")]
    [InlineData("user-input", "{\"event\":\"hand-control-back\"}")]
    [InlineData("proof-status", "{\"state\":\"ready\"}")]
    [InlineData("proof-status", "{\"state\":\"manual-step-complete\"}")]
    [InlineData("proof-status", "{\"state\":\"complete\"}")]
    public void ExactInertPayloadSchemasAreAccepted(string type, string payload)
    {
        var accepted = CreateValidator().TryValidate(
            ApprovedSource,
            ToUtf8(CreateEnvelopeJson(type, payload)),
            out var message,
            out var rejectionCode);

        Assert.True(accepted);
        Assert.NotNull(message);
        Assert.Equal(type, message.Type);
        Assert.Equal(string.Empty, rejectionCode);
    }

    [Fact]
    public void LookalikeOriginIsRejected()
    {
        var validator = CreateValidator();

        var accepted = validator.TryValidate(
            new Uri("https://fixture.example.attacker.test:8443/bridge"),
            ToUtf8(CreateEnvelope("fixture-ready", new { ready = true })),
            out var message,
            out var rejectionCode);

        Assert.False(accepted);
        Assert.Null(message);
        Assert.Equal("untrusted-source", rejectionCode);
    }

    [Theory]
    [InlineData("http://fixture.example:8443/bridge")]
    [InlineData("https://fixture.example:8444/bridge")]
    public void WrongSchemeAndPortAreRejected(string source)
    {
        var accepted = CreateValidator().TryValidate(
            new Uri(source),
            ToUtf8(CreateEnvelope("fixture-ready", new { ready = true })),
            out var message,
            out var rejectionCode);

        Assert.False(accepted);
        Assert.Null(message);
        Assert.Equal("untrusted-source", rejectionCode);
    }

    [Fact]
    public void NullSourceIsRejected()
    {
        var accepted = CreateValidator().TryValidate(
            null,
            ToUtf8(CreateEnvelope("fixture-ready", new { ready = true })),
            out var message,
            out var rejectionCode);

        Assert.False(accepted);
        Assert.Null(message);
        Assert.Equal("untrusted-source", rejectionCode);
    }

    [Fact]
    public void DuplicateEnvelopeKeyIsRejected()
    {
        var validator = CreateValidator();
        var json = $"{{\"version\":1,\"version\":1,\"sessionId\":\"{SessionId}\",\"type\":\"fixture-ready\",\"tabId\":\"{TabId:D}\",\"navigationGeneration\":7,\"payload\":{{\"ready\":true}}}}";

        AssertRejected(validator, json, "invalid-envelope");
    }

    [Theory]
    [InlineData("{\"version\":1,\"sessionId\":\"session-opaque-4eb8f7c2\",\"type\":\"fixture-ready\",\"tabId\":\"51515151-5151-5151-5151-515151515151\",\"navigationGeneration\":7}")]
    [InlineData("{\"version\":1,\"sessionId\":\"session-opaque-4eb8f7c2\",\"type\":\"fixture-ready\",\"tabId\":\"51515151-5151-5151-5151-515151515151\",\"navigationGeneration\":7,\"payload\":{},\"extra\":true}")]
    [InlineData("{\"version\":\"1\",\"sessionId\":\"session-opaque-4eb8f7c2\",\"type\":\"fixture-ready\",\"tabId\":\"51515151-5151-5151-5151-515151515151\",\"navigationGeneration\":7,\"payload\":{}}")]
    public void MissingUnknownAndWrongTypedFieldsAreRejected(string json)
    {
        AssertRejected(CreateValidator(), json, "invalid-envelope");
    }

    [Theory]
    [InlineData("sessionId", "different-session")]
    [InlineData("tabId", "52525252-5252-5252-5252-525252525252")]
    [InlineData("navigationGeneration", "8")]
    public void SessionTabAndNavigationBindingsAreRequired(string field, string replacement)
    {
        var json = CreateEnvelope("proof-status", new { state = "ready" });
        json = field switch
        {
            "sessionId" => json.Replace(SessionId, replacement, StringComparison.Ordinal),
            "tabId" => json.Replace(TabId.ToString("D"), replacement, StringComparison.Ordinal),
            "navigationGeneration" => json.Replace("\"navigationGeneration\":7", "\"navigationGeneration\":" + replacement, StringComparison.Ordinal),
            _ => throw new InvalidOperationException(),
        };

        AssertRejected(CreateValidator(), json, "binding-mismatch");
    }

    [Fact]
    public void UnsupportedTypeDoesNotCreateAHostAction()
    {
        AssertRejected(
            CreateValidator(),
            CreateEnvelope("hostAction", new { command = "navigate" }),
            "unsupported-type");
    }

    [Theory]
    [InlineData("fixture-ready", "{\"ready\":\"true\"}")]
    [InlineData("user-input", "{\"event\":\"request-credentials\"}")]
    [InlineData("proof-status", "{\"state\":\"awaiting-password\"}")]
    [InlineData("proof-status", "{\"state\":\"ready\",\"detail\":\"extra\"}")]
    [InlineData("fixture-ready", "{\"ready\":true,\"ready\":true}")]
    public void PayloadKindEnumsUnknownAndDuplicateFieldsAreRejected(string type, string payload)
    {
        AssertRejected(CreateValidator(), CreateEnvelopeJson(type, payload), "invalid-envelope");
    }

    [Fact]
    public void NestedDuplicatePayloadFieldIsRejected()
    {
        AssertRejected(
            CreateValidator(),
            CreateEnvelopeJson("fixture-ready", "{\"ready\":{\"nested\":{\"value\":1,\"value\":2}}}"),
            "invalid-envelope");
    }

    [Fact]
    public void PayloadMemberAndDepthBoundsFailClosed()
    {
        const string tooManyMembers = "{\"ready\":true,\"a\":true,\"b\":true,\"c\":true,\"d\":true,\"e\":true,\"f\":true,\"g\":true,\"h\":true,\"i\":true,\"j\":true,\"k\":true,\"l\":true,\"m\":true,\"n\":true,\"o\":true,\"p\":true}";
        const string tooDeep = "{\"ready\":{\"one\":{\"two\":{\"three\":{\"four\":true}}}}}";

        AssertRejected(CreateValidator(), CreateEnvelopeJson("fixture-ready", tooManyMembers), "invalid-envelope");
        AssertRejected(CreateValidator(), CreateEnvelopeJson("fixture-ready", tooDeep), "invalid-envelope");
        AssertRejected(CreateValidator(), "{", "invalid-envelope");
    }

    [Fact]
    public void ControlCharactersAndUtf8PayloadLimitAreRejected()
    {
        AssertRejected(
            CreateValidator(),
            CreateEnvelope("proof-status", new { state = "line\nfeed" }),
            "invalid-envelope");

        AssertRejected(
            CreateValidator(),
            CreateEnvelope("proof-status", new { state = new string('\u00e9', 1025) }),
            "invalid-envelope");
    }

    [Fact]
    public void SessionIdentifierControlCharacterAndUtf8BoundaryAreValidated()
    {
        var controlCharacterSession = "session\nidentifier";
        AssertRejected(
            CreateValidator(),
            CreateEnvelope("fixture-ready", new { ready = true }, controlCharacterSession),
            "invalid-envelope");

        var overlongUtf8Session = new string('\u00e9', 65);
        Assert.Equal(130, Encoding.UTF8.GetByteCount(overlongUtf8Session));
        AssertRejected(
            CreateValidator(),
            CreateEnvelope("fixture-ready", new { ready = true }, overlongUtf8Session),
            "invalid-envelope");

        var maximumSession = new string('s', 128);
        Assert.Equal(128, Encoding.UTF8.GetByteCount(maximumSession));
        var accepted = CreateValidator(sessionId: maximumSession).TryValidate(
            ApprovedSource,
            ToUtf8(CreateEnvelope("fixture-ready", new { ready = true }, maximumSession)),
            out var message,
            out var rejectionCode);

        Assert.True(accepted);
        Assert.NotNull(message);
        Assert.Equal(string.Empty, rejectionCode);
    }

    [Fact]
    public void RawFixtureReadyPayloadAt1024BytesIsAcceptedAnd1025BytesIsRejected()
    {
        var atLimit = FixtureReadyPayloadWithRawUtf8Bytes(1024);
        Assert.Equal(1024, Encoding.UTF8.GetByteCount(atLimit));
        var accepted = CreateValidator().TryValidate(
            ApprovedSource,
            ToUtf8(CreateEnvelopeJson("fixture-ready", atLimit)),
            out var message,
            out var rejectionCode);

        Assert.True(accepted);
        Assert.NotNull(message);
        Assert.Equal(string.Empty, rejectionCode);

        var overLimit = FixtureReadyPayloadWithRawUtf8Bytes(1025);
        Assert.Equal(1025, Encoding.UTF8.GetByteCount(overLimit));
        AssertRejected(
            CreateValidator(),
            CreateEnvelopeJson("fixture-ready", overLimit),
            "invalid-envelope");
    }

    [Fact]
    public void TotalEnvelopeAt4096BytesIsAcceptedAnd4097BytesIsRejected()
    {
        var compactEnvelope = CreateEnvelope("fixture-ready", new { ready = true });
        var atLimit = PadEnvelopeWithJsonWhitespace(compactEnvelope, 4096);
        Assert.Equal(4096, Encoding.UTF8.GetByteCount(atLimit));

        var accepted = CreateValidator().TryValidate(
            ApprovedSource,
            ToUtf8(atLimit),
            out var message,
            out var rejectionCode);

        Assert.True(accepted);
        Assert.NotNull(message);
        Assert.Equal(string.Empty, rejectionCode);

        var overLimit = atLimit + " ";
        Assert.Equal(4097, Encoding.UTF8.GetByteCount(overLimit));
        AssertRejected(CreateValidator(), overLimit, "invalid-envelope");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ConstructorRejectsNonPositiveNavigationGeneration(long navigationGeneration)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BrowserBridgeMessageValidator(
                ApprovedUri,
                SessionId,
                TabId,
                navigationGeneration,
                Now.AddMinutes(1),
                new FixedTimeProvider(Now)));
    }

    [Fact]
    public void ConstructorRequiresAFutureUtcExpiryAndClock()
    {
        var clock = new FixedTimeProvider(Now);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BrowserBridgeMessageValidator(ApprovedUri, SessionId, TabId, 7, Now, clock));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BrowserBridgeMessageValidator(
                ApprovedUri,
                SessionId,
                TabId,
                7,
                new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.FromHours(1)),
                clock));
        Assert.Throws<ArgumentNullException>(() =>
            new BrowserBridgeMessageValidator(ApprovedUri, SessionId, TabId, 7, Now.AddMinutes(1), null!));
    }

    [Fact]
    public void ExpiredSessionIsRejectedAtAndAfterExpiry()
    {
        var clock = new MutableTimeProvider(Now);
        var validator = CreateValidator(clock, Now.AddMinutes(1));

        var envelope = ToUtf8(CreateEnvelope("fixture-ready", new { ready = true }));
        Assert.True(validator.TryValidate(ApprovedSource, envelope, out _, out _));

        clock.UtcNow = Now.AddMinutes(1);
        Assert.False(validator.TryValidate(ApprovedSource, envelope, out var atExpiry, out var atExpiryCode));
        Assert.Null(atExpiry);
        Assert.Equal("inactive-session", atExpiryCode);

        clock.UtcNow = Now.AddMinutes(2);
        Assert.False(validator.TryValidate(ApprovedSource, envelope, out var afterExpiry, out var afterExpiryCode));
        Assert.Null(afterExpiry);
        Assert.Equal("inactive-session", afterExpiryCode);
    }

    [Fact]
    public void RejectionNeverEchoesTheSessionOrPayload()
    {
        const string sensitivePayload = "payload-that-must-not-appear";
        var validator = CreateValidator();

        var accepted = validator.TryValidate(
            ApprovedSource,
            ToUtf8(CreateEnvelope("proof-status", new { state = sensitivePayload })),
            out var message,
            out var rejectionCode);

        Assert.False(accepted);
        Assert.Null(message);
        Assert.Equal("invalid-envelope", rejectionCode);
        Assert.DoesNotContain(SessionId, rejectionCode, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitivePayload, rejectionCode, StringComparison.Ordinal);
    }

    private static BrowserBridgeMessageValidator CreateValidator(
        TimeProvider? timeProvider = null,
        DateTimeOffset? expiresAtUtc = null,
        string sessionId = SessionId) =>
        new(
            ApprovedUri,
            sessionId,
            TabId,
            7,
            expiresAtUtc ?? Now.AddMinutes(5),
            timeProvider ?? new FixedTimeProvider(Now));

    private static void AssertRejected(
        BrowserBridgeMessageValidator validator,
        string json,
        string expectedRejectionCode)
    {
        var accepted = validator.TryValidate(ApprovedSource, ToUtf8(json), out var message, out var rejectionCode);

        Assert.False(accepted);
        Assert.Null(message);
        Assert.Equal(expectedRejectionCode, rejectionCode);
    }

    private static string CreateEnvelope(string type, object payload, string? sessionId = null) =>
        CreateEnvelopeJson(type, JsonSerializer.Serialize(payload), sessionId);

    private static string CreateEnvelopeJson(string type, string payload, string? sessionId = null) =>
        $"{{\"version\":1,\"sessionId\":{JsonSerializer.Serialize(sessionId ?? SessionId)},\"type\":{JsonSerializer.Serialize(type)},\"tabId\":{JsonSerializer.Serialize(TabId.ToString("D"))},\"navigationGeneration\":7,\"payload\":{payload}}}";

    private static byte[] ToUtf8(string value) => Encoding.UTF8.GetBytes(value);

    private static string FixtureReadyPayloadWithRawUtf8Bytes(int requiredBytes)
    {
        const string prefix = "{\"ready\"";
        const string suffix = ":true}";
        var fixedBytes = Encoding.UTF8.GetByteCount(prefix + suffix);
        if (requiredBytes < fixedBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(requiredBytes));
        }

        return prefix + new string(' ', requiredBytes - fixedBytes) + suffix;
    }

    private static string PadEnvelopeWithJsonWhitespace(string envelope, int requiredBytes)
    {
        var currentBytes = Encoding.UTF8.GetByteCount(envelope);
        if (requiredBytes < currentBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(requiredBytes));
        }

        return envelope + new string(' ', requiredBytes - currentBytes);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
