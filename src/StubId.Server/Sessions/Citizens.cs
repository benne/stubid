using System.Collections.Concurrent;
using StubId.Wire;

namespace StubId.Server.Sessions;

/// <summary>A person a test can authenticate as.</summary>
/// <param name="Id">Stable and readable, so a test can name one without holding a GUID.</param>
public sealed record Citizen(
    string Id,
    string Uuid,
    string Name,
    string DateOfBirth,
    string Cpr,
    string? UserName = null,
    string Amr = "code_app",
    string Loa = "Substantial",
    string Pid = "9208-2002-2-000000000001",
    string? Rule = null)
{
    /// <summary>
    /// What a login as this person does, whoever chose them. Null approves; anything else is
    /// the broker error code the login fails with.
    /// </summary>
    /// <remarks>
    /// The point of attaching it to the person rather than to the session is that a suite can
    /// set it up once and then write ordinary tests: choosing <c>aborts</c> aborts, whether the
    /// request named them, someone picked them on the login page, or a test approved as them.
    /// A rule is not an override of an approval so much as what approving that person means.
    ///
    /// A queued decision is the exception, and deliberately: it queues an outcome rather than
    /// a person, which makes it the way to approve a rule-bearing citizen anyway.
    /// </remarks>
    public Decision Outcome() =>
        Rule is null ? Decision.Approved(Id) : Decision.Refused(Rule);

    public int Age(DateTimeOffset now)
    {
        var born = DateOnly.Parse(DateOfBirth, System.Globalization.CultureInfo.InvariantCulture);
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var age = today.Year - born.Year;

        return today < born.AddYears(age) ? age - 1 : age;
    }
}

/// <summary>
/// The people this instance can authenticate as.
/// </summary>
/// <remarks>
/// In memory, and deliberately so: a test instance that remembered citizens between runs would
/// make one test depend on another. Persistence arrives with the hosted instance, which has a
/// reason to want it.
/// </remarks>
public sealed class Citizens
{
    private readonly ConcurrentDictionary<string, Citizen> _citizens = new(StringComparer.Ordinal);
    private int _sequence;

    public Citizens()
    {
        // One to authenticate as before anybody creates anything, so a fresh instance is
        // useful without setup.
        Add(new Citizen(
            Id: "default",
            Uuid: "1a5f8c2e-0b47-4d9a-9f31-6c2e8b7a4d15",
            Name: "Anders Berg Christiansen",
            DateOfBirth: "1985-03-29",
            Cpr: Cpr.Generate(new DateOnly(1985, 3, 29), Gender.Male),
            UserName: "anders"));
    }

    public IReadOnlyCollection<Citizen> All => [.. _citizens.Values];

    /// <summary>Who an automatic approval authenticates as. The first one created wins.</summary>
    public Citizen? Default => _citizens.TryGetValue("default", out var citizen)
        ? citizen
        : _citizens.Values.OrderBy(c => c.Id, StringComparer.Ordinal).FirstOrDefault();

    public Citizen? ById(string id) => _citizens.GetValueOrDefault(id);

    public Citizen? ByUuid(string uuid) =>
        _citizens.Values.FirstOrDefault(c => string.Equals(c.Uuid, uuid, StringComparison.OrdinalIgnoreCase));

    public Citizen? ByUserName(string userName) =>
        _citizens.Values.FirstOrDefault(c => string.Equals(c.UserName, userName, StringComparison.OrdinalIgnoreCase));

    public Citizen? ByCpr(string cpr) =>
        _citizens.Values.FirstOrDefault(c => c.Cpr == cpr.Replace("-", "", StringComparison.Ordinal));

    public Citizen Add(Citizen citizen)
    {
        _citizens[citizen.Id] = citizen;
        return citizen;
    }

    /// <summary>
    /// Creates a citizen, inventing what was not given. The personal number is always a
    /// replacement number, which cannot belong to anyone.
    /// </summary>
    public Citizen Create(
        string? id,
        string name,
        DateOnly dateOfBirth,
        Gender gender,
        string? userName = null,
        string? rule = null)
    {
        var sequence = Interlocked.Increment(ref _sequence);
        var citizen = new Citizen(
            Id: id ?? $"citizen-{sequence}",
            Uuid: Uuid5.Create(Namespace, $"{name}|{dateOfBirth:O}|{sequence}").ToString(),
            Name: name,
            DateOfBirth: dateOfBirth.ToString("yyyy-MM-dd"),
            Cpr: Cpr.Generate(dateOfBirth, gender, sequence),
            UserName: userName,
            Rule: rule);

        return Add(citizen);
    }

    public bool Remove(string id) => _citizens.TryRemove(id, out _);

    private static readonly Guid Namespace = new("2a1f6c9d-84b3-4e57-9a20-7d5c1e8f3b64");
}
