using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace WinForge.ActionBroker.V2.Requests;

public sealed class CanonicalValue
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly object? _scalar;

    private CanonicalValue(
        CanonicalValueKind kind,
        object? scalar = null,
        ImmutableArray<CanonicalValue> items = default,
        ImmutableSortedDictionary<string, CanonicalValue>? properties = null)
    {
        Kind = kind;
        _scalar = scalar;
        Items = items.IsDefault ? ImmutableArray<CanonicalValue>.Empty : items;
        Properties = properties ?? ImmutableSortedDictionary.Create<string, CanonicalValue>(StringComparer.Ordinal);
    }

    public CanonicalValueKind Kind { get; }
    public ImmutableArray<CanonicalValue> Items { get; }
    public ImmutableSortedDictionary<string, CanonicalValue> Properties { get; }
    public string StringValue => Kind == CanonicalValueKind.String ? (string)_scalar! : throw WrongKind();
    public bool BooleanValue => Kind == CanonicalValueKind.Boolean ? (bool)_scalar! : throw WrongKind();
    public long IntegerValue => Kind == CanonicalValueKind.Integer ? (long)_scalar! : throw WrongKind();
    public decimal DecimalValue => Kind == CanonicalValueKind.Decimal ? (decimal)_scalar! : throw WrongKind();

    public static CanonicalValue Null() => new(CanonicalValueKind.Null);
    public static CanonicalValue FromBoolean(bool value) => new(CanonicalValueKind.Boolean, value);
    public static CanonicalValue FromInteger(long value) => new(CanonicalValueKind.Integer, value);
    public static CanonicalValue FromDecimal(decimal value) => new(CanonicalValueKind.Decimal, value == 0 ? 0m : value);
    public static CanonicalValue FromString(string value) =>
        new(CanonicalValueKind.String, NormalizeText(value, nameof(value), 8192));

    public static CanonicalValue FromArray(IEnumerable<CanonicalValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var builder = ImmutableArray.CreateBuilder<CanonicalValue>();
        foreach (var item in values)
        {
            if (item is null || builder.Count == 512)
            {
                throw new ArgumentException("Canonical arrays must contain at most 512 non-null values.", nameof(values));
            }

            builder.Add(item);
        }

        return new CanonicalValue(CanonicalValueKind.Array, items: builder.ToImmutable());
    }

    public static CanonicalValue FromObject(IEnumerable<KeyValuePair<string, CanonicalValue>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var properties = ImmutableSortedDictionary.CreateBuilder<string, CanonicalValue>(StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            var normalizedKey = NormalizeText(key, nameof(values), 256);
            if (value is null || properties.Count == 512 || !properties.TryAdd(normalizedKey, value))
            {
                throw new ArgumentException("Canonical objects must have at most 512 unique normalized keys and non-null values.", nameof(values));
            }
        }

        return new CanonicalValue(CanonicalValueKind.Object, properties: properties.ToImmutable());
    }

    internal static string NormalizeText(string value, string parameterName, int maximumUtf8Bytes)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        var normalized = value.Normalize(NormalizationForm.FormC);
        int byteCount;
        try
        {
            byteCount = StrictUtf8.GetByteCount(normalized);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException("Text must contain valid Unicode scalar values.", parameterName, exception);
        }

        if (byteCount > maximumUtf8Bytes)
        {
            throw new ArgumentException($"Value exceeds the {maximumUtf8Bytes}-byte UTF-8 limit.", parameterName);
        }

        return normalized;
    }

    internal string DecimalText => DecimalValue.ToString("G29", CultureInfo.InvariantCulture);

    private InvalidOperationException WrongKind() => new($"Value kind is {Kind}, not the requested scalar kind.");
}

public enum CanonicalValueKind : byte
{
    Null = 0,
    Boolean = 1,
    Integer = 2,
    Decimal = 3,
    String = 4,
    Array = 5,
    Object = 6
}

public sealed record TargetIdentity
{
    private TargetIdentity(string targetType, string identity, string? version)
    {
        TargetType = targetType;
        Identity = identity;
        Version = version;
    }

    public string TargetType { get; }
    public string Identity { get; }
    public string? Version { get; }

    public static TargetIdentity Create(string targetType, string identity, string? version = null)
    {
        var normalizedType = CanonicalValue.NormalizeText(targetType, nameof(targetType), 128);
        var normalizedIdentity = CanonicalValue.NormalizeText(identity, nameof(identity), 2048);
        var normalizedVersion = version is null
            ? null
            : CanonicalValue.NormalizeText(version, nameof(version), 256);
        if (normalizedType.Length == 0 || normalizedIdentity.Length == 0 ||
            normalizedVersion is { Length: 0 })
        {
            throw new ArgumentException("Target type and identity are required; an optional version cannot be empty.");
        }

        return new TargetIdentity(normalizedType, normalizedIdentity, normalizedVersion);
    }
}

public sealed record CallerIdentityClaim(string ClaimType, string ClaimValue)
{
    public static CallerIdentityClaim Create(string claimType, string claimValue) =>
        new(
            CanonicalValue.NormalizeText(claimType, nameof(claimType), 128),
            CanonicalValue.NormalizeText(claimValue, nameof(claimValue), 1024));
}

public sealed record ConnectionIdentity(Guid ConnectionId, Guid EndpointEpoch)
{
    public static ConnectionIdentity Create(Guid connectionId, Guid endpointEpoch)
    {
        if (connectionId == Guid.Empty || endpointEpoch == Guid.Empty)
        {
            throw new ArgumentException("Connection and endpoint epoch identifiers must be non-empty.");
        }

        return new ConnectionIdentity(connectionId, endpointEpoch);
    }
}

public sealed record ProcessInstanceIdentity(uint ProcessId, long ProcessCreationTimeUtcTicks)
{
    public static ProcessInstanceIdentity Create(uint processId, long processCreationTimeUtcTicks)
    {
        if (processId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processId), "Process identity requires a non-zero PID.");
        }

        if (processCreationTimeUtcTicks <= 0 || processCreationTimeUtcTicks > DateTime.MaxValue.Ticks)
        {
            throw new ArgumentOutOfRangeException(nameof(processCreationTimeUtcTicks), "Process creation time must be valid UTC ticks.");
        }

        return new ProcessInstanceIdentity(processId, processCreationTimeUtcTicks);
    }
}

public sealed record LogonSessionContext(string UserSid, ulong AuthenticationId, uint SessionId)
{
    public static LogonSessionContext Create(string userSid, ulong authenticationId, uint sessionId)
    {
        var normalizedSid = CanonicalValue.NormalizeText(userSid, nameof(userSid), 184);
        if (!IsSidShape(normalizedSid))
        {
            throw new ArgumentException("User SID must use the structural S-1-n[-n...] form.", nameof(userSid));
        }

        normalizedSid = "S" + normalizedSid[1..];
        return new LogonSessionContext(normalizedSid, authenticationId, sessionId);
    }

    private static bool IsSidShape(string value)
    {
        if (!value.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var components = value.Split('-');
        return components.Length >= 3 &&
            components.Skip(2).All(static component =>
                component.Length > 0 && component.All(static character => character is >= '0' and <= '9'));
    }
}

public sealed record CallerIdentityEvidence(
    ConnectionIdentity Connection,
    ProcessInstanceIdentity Process,
    LogonSessionContext LogonSession,
    DateTimeOffset CapturedAtUtc)
{
    public const string TrustStatus = "Unverified host-provided model input";

    public static CallerIdentityEvidence CreateUnverified(
        ConnectionIdentity connection,
        ProcessInstanceIdentity process,
        LogonSessionContext logonSession,
        DateTimeOffset capturedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(logonSession);
        var validatedConnection = ConnectionIdentity.Create(connection.ConnectionId, connection.EndpointEpoch);
        var validatedProcess = ProcessInstanceIdentity.Create(process.ProcessId, process.ProcessCreationTimeUtcTicks);
        var validatedLogon = LogonSessionContext.Create(
            logonSession.UserSid,
            logonSession.AuthenticationId,
            logonSession.SessionId);
        return new CallerIdentityEvidence(
            validatedConnection,
            validatedProcess,
            validatedLogon,
            capturedAtUtc.ToUniversalTime());
    }
}

public sealed record RequestIdentityContext
{
    private RequestIdentityContext(
        CallerIdentityEvidence evidence,
        ImmutableArray<CallerIdentityClaim> callerClaims)
    {
        Evidence = evidence;
        CallerClaims = callerClaims;
    }

    public CallerIdentityEvidence Evidence { get; }
    public ImmutableArray<CallerIdentityClaim> CallerClaims { get; }

    public static RequestIdentityContext Create(
        CallerIdentityEvidence evidence,
        IEnumerable<CallerIdentityClaim>? callerClaims = null)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var builder = ImmutableArray.CreateBuilder<CallerIdentityClaim>();
        if (callerClaims is not null)
        {
            foreach (var claim in callerClaims)
            {
                if (claim is null || builder.Count == 64)
                {
                    throw new ArgumentException("Caller claims must contain at most 64 non-null values.", nameof(callerClaims));
                }

                builder.Add(claim);
            }
        }

        var validatedEvidence = CallerIdentityEvidence.CreateUnverified(
            evidence.Connection,
            evidence.Process,
            evidence.LogonSession,
            evidence.CapturedAtUtc);
        var validatedClaims = builder
            .Select(static claim => CallerIdentityClaim.Create(claim.ClaimType, claim.ClaimValue))
            .ToImmutableArray();
        return new RequestIdentityContext(validatedEvidence, validatedClaims);
    }
}

public sealed record AuthorizationContext
{
    private AuthorizationContext(
        Guid authorizationId,
        string policyVersion,
        string previewDigest,
        Guid challenge,
        Guid nonce,
        DateTimeOffset createdAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        AuthorizationId = authorizationId;
        PolicyVersion = policyVersion;
        PreviewDigest = previewDigest;
        Challenge = challenge;
        Nonce = nonce;
        CreatedAtUtc = createdAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    public Guid AuthorizationId { get; }
    public string PolicyVersion { get; }
    public string PreviewDigest { get; }
    public Guid Challenge { get; }
    public Guid Nonce { get; }
    public DateTimeOffset CreatedAtUtc { get; }
    public DateTimeOffset ExpiresAtUtc { get; }

    public static AuthorizationContext Create(
        Guid authorizationId,
        string policyVersion,
        string previewDigest,
        Guid challenge,
        Guid nonce,
        DateTimeOffset createdAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        var normalizedPolicy = CanonicalValue.NormalizeText(policyVersion, nameof(policyVersion), 128);
        var normalizedPreview = CanonicalValue.NormalizeText(previewDigest, nameof(previewDigest), 256);
        if (        authorizationId == Guid.Empty ||
        normalizedPolicy.Length == 0 ||
            normalizedPreview.Length == 0 ||
            challenge == Guid.Empty ||
            nonce == Guid.Empty ||
            expiresAtUtc <= createdAtUtc)
        {
            throw new ArgumentException("Authorization context requires versions, digests, identifiers, and an expiry later than creation.");
        }

        return new AuthorizationContext(
            authorizationId,
            normalizedPolicy,
            normalizedPreview,
            challenge,
            nonce,
            createdAtUtc.ToUniversalTime(),
            expiresAtUtc.ToUniversalTime());
    }
}

public sealed record ActionRequest
{
    private ActionRequest(
        Guid requestId,
        string actionType,
        string catalogVersion,
        TargetIdentity target,
        CanonicalValue parameters,
        AuthorizationContext authorization,
        RequestIdentityContext identity)
    {
        RequestId = requestId;
        ActionType = actionType;
        CatalogVersion = catalogVersion;
        Target = target;
        Parameters = parameters;
        Authorization = authorization;
        Identity = identity;
    }

    public Guid RequestId { get; }
    public string ActionType { get; }
    public string CatalogVersion { get; }
    public TargetIdentity Target { get; }
    public CanonicalValue Parameters { get; }
    public AuthorizationContext Authorization { get; }
    public RequestIdentityContext Identity { get; }

    public static ActionRequest Create(
        Guid requestId,
        string actionType,
        string catalogVersion,
        TargetIdentity target,
        CanonicalValue parameters,
        AuthorizationContext authorization,
        RequestIdentityContext identity)
    {
        if (requestId == Guid.Empty)
        {
            throw new ArgumentException("Request identifier must not be empty.", nameof(requestId));
        }

        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(identity);
        if (parameters.Kind != CanonicalValueKind.Object)
        {
            throw new ArgumentException("Normalized request parameters must be a canonical object.", nameof(parameters));
        }

        ValidateTree(parameters, 0);
        return new ActionRequest(
            requestId,
            CanonicalValue.NormalizeText(actionType, nameof(actionType), 256),
            CanonicalValue.NormalizeText(catalogVersion, nameof(catalogVersion), 128),
            target,
            parameters,
            authorization,
            identity);
    }

    private static void ValidateTree(CanonicalValue value, int depth)
    {
        if (depth > 16)
        {
            throw new ArgumentException("Canonical parameter nesting cannot exceed 16 levels.", nameof(value));
        }

        if (value.Kind == CanonicalValueKind.Array)
        {
            foreach (var item in value.Items)
            {
                ValidateTree(item, depth + 1);
            }
        }
        else if (value.Kind == CanonicalValueKind.Object)
        {
            foreach (var item in value.Properties.Values)
            {
                ValidateTree(item, depth + 1);
            }
        }
    }
}

public enum RequestValidationCode
{
    RequestExpired,
    RequestNotYetValid,
    EmptyActionType,
    EmptyCatalogVersion,
    EmptyTargetType,
    EmptyTargetIdentity
}

public sealed record RequestValidationError(RequestValidationCode Code, string Message);

public static class ActionRequestValidator
{
    public static ImmutableArray<RequestValidationError> ValidateAt(ActionRequest request, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = ImmutableArray.CreateBuilder<RequestValidationError>();
        if (request.ActionType.Length == 0)
        {
            errors.Add(new(RequestValidationCode.EmptyActionType, "Action type is empty."));
        }

        if (request.CatalogVersion.Length == 0)
        {
            errors.Add(new(RequestValidationCode.EmptyCatalogVersion, "Catalog version is empty."));
        }

        if (request.Target.Identity.Length == 0)
        {
            errors.Add(new(RequestValidationCode.EmptyTargetIdentity, "Target identity is empty."));
        }

        if (request.Target.TargetType.Length == 0)
        {
            errors.Add(new(RequestValidationCode.EmptyTargetType, "Target type is empty."));
        }

        var now = nowUtc.ToUniversalTime();
        if (now < request.Authorization.CreatedAtUtc)
        {
            errors.Add(new(RequestValidationCode.RequestNotYetValid, "Request is not yet valid."));
        }
        else if (now >= request.Authorization.ExpiresAtUtc)
        {
            errors.Add(new(RequestValidationCode.RequestExpired, "Request has expired."));
        }

        return errors.ToImmutable();
    }
}
