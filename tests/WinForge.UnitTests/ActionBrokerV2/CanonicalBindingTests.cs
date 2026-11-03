using System.Globalization;
using WinForge.ActionBroker.V2.Binding;
using WinForge.ActionBroker.V2.Requests;

namespace WinForge.UnitTests.ActionBrokerV2;

public sealed class CanonicalBindingTests
{
    [Fact]
    public void Parameter_order_and_equivalent_normalized_values_have_same_binding()
    {
        var first = CanonicalValue.FromObject(
        [
            new("z", CanonicalValue.FromDecimal(1.2300m)),
            new("cafe\u0301", CanonicalValue.FromString("e\u0301"))
        ]);
        var second = CanonicalValue.FromObject(
        [
            new("café", CanonicalValue.FromString("é")),
            new("z", CanonicalValue.FromDecimal(1.23m))
        ]);

        var firstDigest = CanonicalRequestBinder.ComputeDigest(ActionBrokerV2TestData.Request(parameters: first));
        var secondDigest = CanonicalRequestBinder.ComputeDigest(ActionBrokerV2TestData.Request(parameters: second));

        Assert.Equal(firstDigest, secondDigest);
    }

    [Fact]
    public void Changed_action_catalog_target_parameters_policy_preview_or_challenge_changes_binding()
    {
        var original = ActionBrokerV2TestData.Request();
        var digest = CanonicalRequestBinder.ComputeDigest(original);
        var changed = new[]
        {
            ActionBrokerV2TestData.Request(actionType: "user-preference.delete"),
            ActionBrokerV2TestData.Request(catalogVersion: "2.0"),
            ActionBrokerV2TestData.Request(target: TargetIdentity.Create("preference", "notifications", "v1")),
            ActionBrokerV2TestData.Request(parameters: ActionBrokerV2TestData.Parameters(value: CanonicalValue.FromString("light"))),
            ActionBrokerV2TestData.Request(policyVersion: "policy-2"),
            ActionBrokerV2TestData.Request(previewDigest: "preview-digest-2"),
            ActionBrokerV2TestData.Request(authorizationId: Guid.Parse("20000000-0000-0000-0000-000000000011")),
            ActionBrokerV2TestData.Request(challenge: Guid.Parse("20000000-0000-0000-0000-000000000001")),
            ActionBrokerV2TestData.Request(nonce: Guid.Parse("20000000-0000-0000-0000-000000000002"))
        };

        Assert.All(changed, request => Assert.NotEqual(digest, CanonicalRequestBinder.ComputeDigest(request)));
    }

    [Fact]
    public void Missing_parameter_and_explicit_null_are_distinct()
    {
        var missing = CanonicalValue.FromObject([]);
        var explicitNull = CanonicalValue.FromObject(
        [
            new KeyValuePair<string, CanonicalValue>("value", CanonicalValue.Null())
        ]);

        Assert.NotEqual(
            CanonicalRequestBinder.ComputeDigest(ActionBrokerV2TestData.Request(parameters: missing)),
            CanonicalRequestBinder.ComputeDigest(ActionBrokerV2TestData.Request(parameters: explicitNull)));
    }

    [Fact]
    public void Caller_claims_are_not_authoritative_binding_inputs_but_host_evidence_is_bound()
    {
        var request = ActionBrokerV2TestData.Request();
        var callerClaimChanged = ActionBrokerV2TestData.Request(
            identity: ActionBrokerV2TestData.Identity(
                claims: [CallerIdentityClaim.Create("userSid", "S-1-5-21-attacker-claim")]));
        var hostEvidenceChanged = ActionBrokerV2TestData.Request(
            identity: ActionBrokerV2TestData.Identity(logonLuid: 987654322));

        Assert.Equal(
            CanonicalRequestBinder.ComputeDigest(request),
            CanonicalRequestBinder.ComputeDigest(callerClaimChanged));
        Assert.NotEqual(
            CanonicalRequestBinder.ComputeDigest(request),
            CanonicalRequestBinder.ComputeDigest(hostEvidenceChanged));
        Assert.Equal("Unverified host-provided model input", CallerIdentityEvidence.TrustStatus);
    }

    [Fact]
    public void Identity_revalidation_detects_reconnect_pid_reuse_logoff_and_session_change()
    {
        var original = ActionBrokerV2TestData.Identity().Evidence;
        var sameContextRecaptured = CallerIdentityEvidence.CreateUnverified(
            original.Connection,
            original.Process,
            original.LogonSession,
            original.CapturedAtUtc.AddSeconds(1));
        var reconnect = ActionBrokerV2TestData.Identity(
            connectionId: Guid.Parse("20000000-0000-0000-0000-000000000010")).Evidence;
        var pidReused = ActionBrokerV2TestData.Identity(processCreatedTicks: original.Process.ProcessCreationTimeUtcTicks + 1).Evidence;
        var differentLogon = ActionBrokerV2TestData.Identity(logonLuid: original.LogonSession.AuthenticationId + 1).Evidence;
        var differentSession = ActionBrokerV2TestData.Identity(sessionId: original.LogonSession.SessionId + 1).Evidence;

        Assert.True(CanonicalRequestBinder.IsSameConnection(original, sameContextRecaptured));
        Assert.True(CanonicalRequestBinder.IsSamePrincipalAndSession(original, sameContextRecaptured));
        Assert.False(CanonicalRequestBinder.IsSameConnection(original, reconnect));
        Assert.False(CanonicalRequestBinder.IsSameConnection(original, pidReused));
        Assert.False(CanonicalRequestBinder.IsSamePrincipalAndSession(original, differentLogon));
        Assert.False(CanonicalRequestBinder.IsSamePrincipalAndSession(original, differentSession));
        Assert.Equal(
            ActionBrokerV2TestData.Identity(sid: "s-1-5-21-10-20-30-1001").Evidence.LogonSession.UserSid,
            ActionBrokerV2TestData.Identity().Evidence.LogonSession.UserSid);
    }

    [Fact]
    public void Digest_is_stable_across_culture_and_uses_explicit_sha256_prefix()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var first = CanonicalRequestBinder.ComputeDigest(
                ActionBrokerV2TestData.Request(parameters: ActionBrokerV2TestData.Parameters(value: CanonicalValue.FromDecimal(12.50m))));
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            var second = CanonicalRequestBinder.ComputeDigest(
                ActionBrokerV2TestData.Request(parameters: ActionBrokerV2TestData.Parameters(value: CanonicalValue.FromDecimal(12.5m))));

            Assert.StartsWith("sha256:", first, StringComparison.Ordinal);
            Assert.Equal(first, second);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void Duplicate_object_keys_after_unicode_normalization_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => CanonicalValue.FromObject(
        [
            new KeyValuePair<string, CanonicalValue>("cafe\u0301", CanonicalValue.Null()),
            new KeyValuePair<string, CanonicalValue>("café", CanonicalValue.Null())
        ]));
    }

    [Fact]
    public void Malformed_identity_and_parameter_shapes_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => LogonSessionContext.Create("not-a-sid", 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProcessInstanceIdentity.Create(0, 100));
        Assert.Throws<ArgumentException>(() => ActionBrokerV2TestData.Request(parameters: CanonicalValue.FromString("not-an-object")));
        Assert.Throws<ArgumentException>(() => CanonicalValue.FromArray(Enumerable.Repeat(CanonicalValue.Null(), 513)));
        var overNested = CanonicalValue.Null();
        for (var index = 0; index < 18; index++)
        {
            overNested = CanonicalValue.FromArray([overNested]);
        }

        Assert.Throws<ArgumentException>(() => ActionBrokerV2TestData.Request(
            parameters: CanonicalValue.FromObject([new("nested", overNested)])));
    }

    [Fact]
    public void Request_validator_reports_missing_identifiers_and_expiry_boundaries()
    {
        var emptyIdentifiers = ActionBrokerV2TestData.Request(actionType: "", catalogVersion: "");
        var expired = ActionBrokerV2TestData.Request(expiresAt: ActionBrokerV2TestData.Now);
        var notYetValid = ActionBrokerV2TestData.Request(
            createdAt: ActionBrokerV2TestData.Now.AddMinutes(1),
            expiresAt: ActionBrokerV2TestData.Now.AddMinutes(2));

        Assert.Contains(
            ActionRequestValidator.ValidateAt(emptyIdentifiers, ActionBrokerV2TestData.Now),
            static error => error.Code == RequestValidationCode.EmptyActionType);
        Assert.Contains(
            ActionRequestValidator.ValidateAt(emptyIdentifiers, ActionBrokerV2TestData.Now),
            static error => error.Code == RequestValidationCode.EmptyCatalogVersion);
        Assert.Contains(
            ActionRequestValidator.ValidateAt(expired, ActionBrokerV2TestData.Now),
            static error => error.Code == RequestValidationCode.RequestExpired);
        Assert.Contains(
            ActionRequestValidator.ValidateAt(notYetValid, ActionBrokerV2TestData.Now),
            static error => error.Code == RequestValidationCode.RequestNotYetValid);
    }
}
