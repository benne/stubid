using System.Text.Json.Serialization;

namespace StubId.Client;

/// <summary>Every shape this client puts on the wire or reads off it.</summary>
/// <remarks>
/// Source-generated rather than reflective because this ships to other people: it is trim- and
/// AOT-safe, so a consumer publishing trimmed gets no warning out of us, and a type that cannot be
/// serialised fails our build rather than their run.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(StubIdCitizen))]
[JsonSerializable(typeof(IReadOnlyList<StubIdCitizen>))]
[JsonSerializable(typeof(StubIdSession))]
[JsonSerializable(typeof(IReadOnlyList<StubIdSession>))]
[JsonSerializable(typeof(SessionExplanation))]
[JsonSerializable(typeof(StubIdClock))]
[JsonSerializable(typeof(StubIdApproval))]
[JsonSerializable(typeof(AutomaticApprovalBody))]
[JsonSerializable(typeof(QueuedBody))]
[JsonSerializable(typeof(ClientsBody))]
[JsonSerializable(typeof(IssuedBody))]
[JsonSerializable(typeof(RoutesBody))]
[JsonSerializable(typeof(SetRuleBody))]
[JsonSerializable(typeof(CreateCitizenBody))]
[JsonSerializable(typeof(ApproveBody))]
[JsonSerializable(typeof(RejectBody))]
[JsonSerializable(typeof(EnqueueBody))]
[JsonSerializable(typeof(AdvanceBody))]
[JsonSerializable(typeof(PublicBaseUrlBody))]
[JsonSerializable(typeof(TlsCertificateBody))]
[JsonSerializable(typeof(DecidedBody))]
[JsonSerializable(typeof(ConflictBody))]
[JsonSerializable(typeof(FaultBody))]
[JsonSerializable(typeof(NowBody))]
[JsonSerializable(typeof(EntriesBody))]
internal sealed partial class ControlJson : JsonSerializerContext;

// The bodies as the control API writes them, kept apart from the shapes a caller sees. The server
// answers a decision three different ways depending on what happened, and a caller should meet one
// type rather than three - keeping these here is what lets that mapping live in one place.

internal sealed record CreateCitizenBody(
    string Name, string DateOfBirth, string? Gender, string? Id, string? UserName, string? Rule);

internal sealed record ApproveBody(string? CitizenId);

internal sealed record RejectBody(string? ErrorCode, string? Error);

internal sealed record EnqueueBody(
    bool Approve, string? ClientId, string? CitizenId, string? ErrorCode, string? Error);

internal sealed record AdvanceBody(double Seconds);

internal sealed record PublicBaseUrlBody(string? PublicBaseUrl);

internal sealed record TlsCertificateBody(string? Certificate, string? Thumbprint);

internal sealed record DecidedBody(bool Decided, string? Citizen, SessionState? State);

internal sealed record ConflictBody(bool Decided, string? Detail, StubIdSession? Outcome);

internal sealed record FaultBody(string? Error, string? Detail);

internal sealed record NowBody(DateTimeOffset Now);

internal sealed record SetRuleBody(string? Rule);

internal sealed record AutomaticApprovalBody(bool? Enabled);

internal sealed record QueuedBody(IReadOnlyList<QueuedDecision> Queued);

internal sealed record ClientsBody(IReadOnlyList<RegisteredClient> Clients);

internal sealed record IssuedBody(IReadOnlyList<IssuedArtefact> Issued);

internal sealed record RoutesBody(IReadOnlyList<EmulatedRoute> Routes);

internal sealed record EntriesBody(IReadOnlyList<FidelityEntry> Entries);
