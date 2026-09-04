using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;

namespace StubId.Server.Admin;

/// <summary>Markup that is already safe to write into a page.</summary>
/// <remarks>
/// The broker's own pages get away with escaping by hand: there are five of them, each renders one
/// or two values, and <see cref="Endpoints" /> injects its body raw because every caller happens to
/// remember. The admin pages render client-controlled strings throughout - transaction texts,
/// client ids, redirect URIs, error codes, the names somebody typed into the citizen form - and
/// twenty pages will not all remember.
/// <para>
/// So escaping is a type here rather than a habit. A <see cref="string" /> interpolated into
/// <see cref="Markup.H" /> is encoded; an <see cref="Html" /> is passed through; and the only way
/// to emit unescaped text is to construct one deliberately, which is a single search away from
/// anyone reviewing this.
/// </para>
/// </remarks>
internal readonly record struct Html(string Value)
{
    public static Html Empty { get; } = new(string.Empty);

    public override string ToString() => Value;
}

/// <summary>Builds <see cref="Html" /> out of an interpolated string, encoding as it goes.</summary>
[InterpolatedStringHandler]
internal ref struct HtmlHandler
{
    private readonly StringBuilder _written;

    public HtmlHandler(int literalLength, int formattedCount) =>
        _written = new StringBuilder(literalLength + (formattedCount * 16));

    internal readonly string Value => _written.ToString();

    public readonly void AppendLiteral(string value) => _written.Append(value);

    /// <summary>Already markup, so it is written as it is.</summary>
    public readonly void AppendFormatted(Html value) => _written.Append(value.Value);

    /// <summary>Text, so it is encoded. This is the overload that makes the rest safe.</summary>
    public readonly void AppendFormatted(string? value) => _written.Append(WebUtility.HtmlEncode(value));

    /// <summary>
    /// Everything else, by way of its invariant form.
    /// </summary>
    /// <remarks>
    /// The culture is named rather than left ambient because <c>InvariantGlobalization</c> is off
    /// in this repository, on purpose: the identities StubID serves carry Danish names and dates.
    /// A timestamp left to the ambient culture would render one way on a developer's machine and
    /// another on a runner, and the assertion that passed locally would fail in CI for a reason
    /// that looks like nothing to do with the page.
    /// </remarks>
    public readonly void AppendFormatted<T>(T value) => AppendFormatted(
        value is IFormattable formattable
            ? formattable.ToString(format: null, CultureInfo.InvariantCulture)
            : value?.ToString());
}

/// <summary>The two ways markup is made.</summary>
internal static class Markup
{
    /// <summary>An interpolated string, with its holes encoded unless they are already markup.</summary>
    public static Html H(HtmlHandler markup) => new(markup.Value);

    /// <summary>Rows, cells, list items: many pieces of markup in the order they were made.</summary>
    public static Html Join(IEnumerable<Html> parts) =>
        new(string.Concat(parts.Select(part => part.Value)));

    /// <summary>One string, encoded, for the places that hold a value without wrapping it.</summary>
    public static Html Text(string? value) => new(WebUtility.HtmlEncode(value) ?? string.Empty);
}
