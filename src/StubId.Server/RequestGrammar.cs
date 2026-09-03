using System.Text.Json;
using StubId.Abstractions;

namespace StubId.Server;

/// <summary>
/// The broker's own parameters, and which of them it refuses a request over.
/// </summary>
/// <remarks>
/// <para>
/// What is validated here and what is carried through is not a judgement call: it is recorded.
/// The broker rejects an <c>idp_values</c> it does not know (CAP-009) and an <c>idp_params</c>
/// that is not JSON (CAP-040), but accepts a malformed value <em>inside</em> a well-formed
/// <c>idp_params</c> (CAP-010) and a simulation mode it does not define (CAP-013). Copying
/// that shape matters: a stub that validated more would fail requests the broker accepts, and
/// a team would spend an afternoon fixing code that was never broken.
/// </para>
/// <para>
/// Scope is the one that would have been guessed wrong. Discovery publishes no
/// <c>scopes_supported</c>, so there is nothing to check a scope against, and the reasonable
/// assumption was that a missing one is carried through. CAP-043 says otherwise: the request
/// is refused outright.
/// </para>
/// </remarks>
public static class RequestGrammar
{
    /// <summary>
    /// The identity providers the broker names in its own error catalogue. An unknown value is
    /// refused, which CAP-009 establishes; these two are what CAP-007 and CAP-041 leave.
    /// </summary>
    private static readonly string[] KnownIdentityProviders = ["mitid", "mitid_erhverv"];

    /// <summary>Why a request cannot proceed, or null when it can.</summary>
    /// <remarks>
    /// The order is not observable: every recording carries one fault at a time, so no
    /// recording says which the broker complains about first. Client, then redirect, then the
    /// rest is the order a reader expects.
    /// </remarks>
    [Fidelity(FidelityTier.Exact, FidelityProvenance.VerifiedLive,
        Evidence = "fixtures/neb/pp/CAP-009, fixtures/neb/pp/CAP-040, fixtures/neb/pp/CAP-043")]
    public static (string Code, string Description)? Fault(
        AuthorizationRequest request, IReadOnlyDictionary<string, string> parameters)
    {
        if (string.IsNullOrEmpty(request.Scope))
        {
            return ("invalid_request", "Missing scope.");
        }

        foreach (var idp in IdentityProviders(parameters))
        {
            if (!KnownIdentityProviders.Contains(idp, StringComparer.Ordinal))
            {
                return ("invalid_request", $"Unknown idp_values entry '{idp}'.");
            }
        }

        if (parameters.TryGetValue("idp_params", out var raw) && raw.Length > 0 && !IsJsonObject(raw))
        {
            // The broker's own code for it: "Typically, invalid encoding of this parameter."
            return ("invalid_idp_params", "idp_params is not URL-encoded JSON.");
        }

        return null;
    }

    /// <summary>A space-delimited list, which is how the broker spells more than one.</summary>
    public static IReadOnlyList<string> IdentityProviders(IReadOnlyDictionary<string, string> parameters) =>
        parameters.TryGetValue("idp_values", out var value)
            ? value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];

    /// <summary>
    /// The per-provider object, decoded but not inspected. CAP-010 records a nonsense
    /// <c>uuid_hint</c> being accepted here and failing later, inside the MitID flow, which is
    /// why the broker publishes an error code for it at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Decoded but not inspected is the whole rule. A value this does not recognise is carried
    /// through and handed on, because that is what CAP-010 records the broker doing - refusing
    /// one up front would fail a request the broker accepts.
    /// </para>
    /// <para>
    /// What is recorded is that a well-formed object with a nonsense value inside it is accepted
    /// (CAP-010) and that idp_params which is not JSON is refused (CAP-040). Everything between
    /// those - a root that parses and is not an object, a name repeated inside the section, a
    /// string that parses and cannot be materialised - is unrecorded, and this answers with
    /// nothing rather than throwing. That is StubID's choice, and the reason for it is that the
    /// alternative is an empty 500, which is the one answer the broker never gives.
    /// </para>
    /// </remarks>
    [Fidelity(FidelityTier.Exact, FidelityProvenance.VerifiedLive,
        Evidence = "fixtures/neb/pp/CAP-010/response.head, "
                   + "fixtures/neb/pp-session/CAP-022/callback/meta.json")]
    public static IReadOnlyDictionary<string, string> IdentityProviderParameters(
        IReadOnlyDictionary<string, string> parameters, string idp)
    {
        if (!parameters.TryGetValue("idp_params", out var raw) || raw.Length == 0)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var found = new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            using var document = JsonDocument.Parse(raw);

            // The root kind is checked rather than assumed. TryGetProperty does not answer false
            // for a root that is not an object - it throws - so idp_params carrying null, a
            // number, a string or an array would escape as an unhandled exception, and the
            // request would answer 500 where the broker answers its error page.
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty(idp, out var section)
                || section.ValueKind != JsonValueKind.Object)
            {
                return found;
            }

            foreach (var member in section.EnumerateObject())
            {
                // Assigned rather than added. JSON allows a name to repeat and JsonDocument keeps
                // both, where ToDictionary throws on the second - so a section with one member
                // written twice would take down the endpoint. The last wins, which is what most
                // JSON readers do; nothing recorded says what the broker does with a repeat.
                found[member.Name] = member.Value.ValueKind == JsonValueKind.String
                    ? member.Value.GetString()!
                    : member.Value.ToString();
            }

            return found;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            // JsonException is malformed JSON. InvalidOperationException is a value that parsed
            // and cannot be read - an unpaired UTF-16 surrogate escape is accepted by the parser
            // and throws only when the string is materialised. Both mean the same thing here:
            // nothing usable, carried through rather than refused, exactly as an unreadable value
            // is on every other path.
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static bool IsJsonObject(string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
