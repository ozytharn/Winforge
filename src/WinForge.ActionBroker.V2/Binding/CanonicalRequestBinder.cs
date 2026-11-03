using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using WinForge.ActionBroker.V2.Requests;

namespace WinForge.ActionBroker.V2.Binding;

public static class CanonicalRequestBinder
{
    private static readonly byte[] Domain = Encoding.ASCII.GetBytes("WinForge.ActionBinding.V2\0");
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] Encode(ActionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var stream = new MemoryStream();
        stream.Write(Domain);
        WriteGuid(stream, 1, request.RequestId);
        WriteString(stream, 2, request.ActionType);
        WriteString(stream, 3, request.CatalogVersion);
        WriteString(stream, 4, request.Target.TargetType);
        WriteString(stream, 5, request.Target.Identity);
        WriteNullableString(stream, 6, request.Target.Version);
        WriteValueField(stream, 7, request.Parameters);
        WriteString(stream, 8, request.Authorization.PolicyVersion);
        WriteString(stream, 9, request.Authorization.PreviewDigest);
        WriteGuid(stream, 10, request.Authorization.AuthorizationId);
        WriteGuid(stream, 11, request.Authorization.Challenge);
        WriteGuid(stream, 12, request.Authorization.Nonce);
        WriteInt64Field(stream, 13, request.Authorization.CreatedAtUtc.UtcTicks);
        WriteInt64Field(stream, 14, request.Authorization.ExpiresAtUtc.UtcTicks);
        WriteIdentity(stream, request.Identity.Evidence);
        return stream.ToArray();
    }

    public static string ComputeDigest(ActionRequest request) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encode(request)));

    public static string ComputeIdentityDigest(CallerIdentityEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        using var stream = new MemoryStream();
        stream.Write(Encoding.ASCII.GetBytes("WinForge.IdentityBinding.V2\0"));
        WriteIdentity(stream, evidence, includeCaptureTime: false);
        return "sha256:" + Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    public static bool IsSameConnection(CallerIdentityEvidence original, CallerIdentityEvidence current)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(current);
        return original.Connection == current.Connection &&
            original.Process == current.Process &&
            original.LogonSession == current.LogonSession;
    }

    public static bool IsSamePrincipalAndSession(CallerIdentityEvidence original, CallerIdentityEvidence current)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(current);
        return StringComparer.Ordinal.Equals(original.LogonSession.UserSid, current.LogonSession.UserSid) &&
            original.LogonSession.AuthenticationId == current.LogonSession.AuthenticationId &&
            original.LogonSession.SessionId == current.LogonSession.SessionId;
    }

    private static void WriteIdentity(
        Stream stream,
        CallerIdentityEvidence evidence,
        bool includeCaptureTime = true)
    {
        WriteGuid(stream, 20, evidence.Connection.ConnectionId);
        WriteGuid(stream, 21, evidence.Connection.EndpointEpoch);
        WriteUInt32Field(stream, 22, evidence.Process.ProcessId);
        WriteInt64Field(stream, 23, evidence.Process.ProcessCreationTimeUtcTicks);
        WriteString(stream, 24, evidence.LogonSession.UserSid);
        WriteUInt64Field(stream, 25, evidence.LogonSession.AuthenticationId);
        WriteUInt32Field(stream, 26, evidence.LogonSession.SessionId);
        if (includeCaptureTime)
        {
            WriteInt64Field(stream, 27, evidence.CapturedAtUtc.UtcTicks);
        }
    }

    private static void WriteGuid(Stream stream, byte tag, Guid value) =>
        WriteField(stream, tag, Encoding.ASCII.GetBytes(value.ToString("N")));

    private static void WriteString(Stream stream, byte tag, string value) =>
        WriteField(stream, tag, StrictUtf8.GetBytes(value));

    private static void WriteNullableString(Stream stream, byte tag, string? value)
    {
        using var payload = new MemoryStream();
        payload.WriteByte(value is null ? (byte)0 : (byte)1);
        if (value is not null)
        {
            WriteLengthPrefixedBytes(payload, StrictUtf8.GetBytes(value));
        }

        WriteField(stream, tag, payload.ToArray());
    }

    private static void WriteInt64Field(Stream stream, byte tag, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        WriteField(stream, tag, bytes);
    }

    private static void WriteUInt32Field(Stream stream, byte tag, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        WriteField(stream, tag, bytes);
    }

    private static void WriteUInt64Field(Stream stream, byte tag, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        WriteField(stream, tag, bytes);
    }

    private static void WriteValueField(Stream stream, byte tag, CanonicalValue value)
    {
        using var payload = new MemoryStream();
        WriteValue(payload, value);
        WriteField(stream, tag, payload.ToArray());
    }

    private static void WriteValue(Stream stream, CanonicalValue value)
    {
        using var payload = new MemoryStream();
        switch (value.Kind)
        {
            case CanonicalValueKind.Null:
                break;
            case CanonicalValueKind.Boolean:
                payload.WriteByte(value.BooleanValue ? (byte)1 : (byte)0);
                break;
            case CanonicalValueKind.Integer:
                WriteInt64(payload, value.IntegerValue);
                break;
            case CanonicalValueKind.Decimal:
                payload.Write(StrictUtf8.GetBytes(value.DecimalText));
                break;
            case CanonicalValueKind.String:
                payload.Write(StrictUtf8.GetBytes(value.StringValue));
                break;
            case CanonicalValueKind.Array:
                WriteInt32(payload, value.Items.Length);
                foreach (var item in value.Items)
                {
                    WriteValue(payload, item);
                }

                break;
            case CanonicalValueKind.Object:
                WriteInt32(payload, value.Properties.Count);
                foreach (var (key, item) in value.Properties)
                {
                    WriteLengthPrefixedBytes(payload, StrictUtf8.GetBytes(key));
                    WriteValue(payload, item);
                }

                break;
            default:
                throw new InvalidOperationException($"Unsupported canonical value kind {value.Kind}.");
        }

        stream.WriteByte((byte)value.Kind);
        WriteLengthPrefixedBytes(stream, payload.ToArray());
    }

    private static void WriteField(Stream stream, byte tag, ReadOnlySpan<byte> payload)
    {
        stream.WriteByte(tag);
        WriteLengthPrefixedBytes(stream, payload);
    }

    private static void WriteLengthPrefixedBytes(Stream stream, ReadOnlySpan<byte> bytes)
    {
        WriteInt32(stream, bytes.Length);
        stream.Write(bytes);
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteInt64(Stream stream, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        stream.Write(bytes);
    }
}
