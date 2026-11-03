using WinForge.ActionBroker.V2.Requests;

namespace WinForge.UnitTests.ActionBrokerV2;

internal static class ActionBrokerV2TestData
{
    public static readonly DateTimeOffset Now = new(2026, 2, 1, 12, 0, 0, TimeSpan.Zero);
    public static readonly Guid RequestId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    public static readonly Guid ApprovalId = Guid.Parse("10000000-0000-0000-0000-000000000002");
    public static readonly Guid ConnectionId = Guid.Parse("10000000-0000-0000-0000-000000000003");
    public static readonly Guid EndpointEpoch = Guid.Parse("10000000-0000-0000-0000-000000000004");
    public static readonly Guid Challenge = Guid.Parse("10000000-0000-0000-0000-000000000005");
    public static readonly Guid Nonce = Guid.Parse("10000000-0000-0000-0000-000000000006");
    public static readonly Guid CorrelationId = Guid.Parse("10000000-0000-0000-0000-000000000007");
    public static readonly Guid StepId = Guid.Parse("10000000-0000-0000-0000-000000000008");
    public static readonly Guid AttemptId = Guid.Parse("10000000-0000-0000-0000-000000000009");
    public static readonly Guid AuthorizationId = Guid.Parse("10000000-0000-0000-0000-000000000010");

    public static ActionRequest Request(
        Guid? requestId = null,
        string actionType = "user-preference.update",
        string catalogVersion = "1.0",
        TargetIdentity? target = null,
        CanonicalValue? parameters = null,
        string policyVersion = "policy-1",
        string previewDigest = "preview-digest-1",
        Guid? authorizationId = null,
        Guid? challenge = null,
        Guid? nonce = null,
        RequestIdentityContext? identity = null,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? expiresAt = null)
    {
        var created = createdAt ?? Now.AddMinutes(-1);
        var auth = AuthorizationContext.Create(
            authorizationId ?? AuthorizationId,
            policyVersion,
            previewDigest,
            challenge ?? Challenge,
            nonce ?? Nonce,
            created,
            expiresAt ?? Now.AddMinutes(5));
        return ActionRequest.Create(
            requestId ?? RequestId,
            actionType,
            catalogVersion,
            target ?? TargetIdentity.Create("preference", "theme", "v1"),
            parameters ?? Parameters(),
            auth,
            identity ?? Identity());
    }

    public static RequestIdentityContext Identity(
        Guid? connectionId = null,
        Guid? endpointEpoch = null,
        uint processId = 1234,
        long processCreatedTicks = 638700000000000000,
        string sid = "S-1-5-21-10-20-30-1001",
        ulong logonLuid = 987654321,
        uint sessionId = 1,
        IEnumerable<CallerIdentityClaim>? claims = null)
    {
        var evidence = CallerIdentityEvidence.CreateUnverified(
            ConnectionIdentity.Create(connectionId ?? ConnectionId, endpointEpoch ?? EndpointEpoch),
            ProcessInstanceIdentity.Create(processId, processCreatedTicks),
            LogonSessionContext.Create(sid, logonLuid, sessionId),
            Now);
        return RequestIdentityContext.Create(evidence, claims);
    }

    public static CanonicalValue Parameters(
        string key = "theme",
        CanonicalValue? value = null) =>
        CanonicalValue.FromObject(
        [
            new KeyValuePair<string, CanonicalValue>(key, value ?? CanonicalValue.FromString("dark"))
        ]);
}
