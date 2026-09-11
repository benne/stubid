namespace StubId.CaptureHarness;

/// <summary>Which broker a setting, a placeholder or a recording belongs to.</summary>
/// <remarks>
/// Redaction is the first part of this harness to need the distinction, and it needs it before
/// the rest does. The catalogs, the fixture root and the sitting are still written for one
/// broker; a value that identifies somebody's account is not something to leave sitting in a
/// single-broker vocabulary until the code around it has caught up.
/// </remarks>
public enum Broker
{
    /// <summary>Signaturgruppen's Nets eID Broker, on its shared public pre-production host.</summary>
    NetsEidBroker,

    /// <summary>Signicat's Digital Trust Platform, where the account is the hostname.</summary>
    Signicat,
}
