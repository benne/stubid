using System.Reflection;
using System.Runtime.CompilerServices;

namespace StubId.Release.Tests;

/// <summary>
/// The public surface of every package this repository publishes, rendered as text.
/// </summary>
/// <remarks>
/// Written out so that a change to it is a diff somebody reads rather than a discovery somebody
/// makes. Nothing else in the tree notices a removed public member: the six aliases deleted in
/// #60 went without a single test failing, because the only thing that had used them was deleted
/// in the same commit.
/// <para>
/// Two decisions keep the file stable against changes that are not ours. Members are read
/// <see cref="BindingFlags.DeclaredOnly" />, because <c>StubIdBuilder</c> and
/// <c>StubIdContainer</c> derive from Testcontainers base classes and inheriting their surface
/// would rewrite this file whenever that package moved. And a type is never rendered through
/// <see cref="Type.FullName" />, which is assembly-qualified for a constructed generic - it would
/// put <c>Version=10.0.0.0</c> into every line holding an <c>IReadOnlyList&lt;string&gt;</c> and
/// rewrite the whole file on a target-framework bump.
/// </para>
/// <para>
/// What it does not render, said plainly rather than left to be discovered: nullability inside a
/// generic argument (the outer annotation is rendered, so <c>string?</c> is distinguished from
/// <c>string</c> but <c>List&lt;string?&gt;</c> is not from <c>List&lt;string&gt;</c>); operators,
/// which share <see cref="MethodBase.IsSpecialName" /> with the accessors this filters out; and
/// the type arguments of a nested type inside a generic one, of which there are none here.
/// </para>
/// </remarks>
internal static class PublicSurface
{
    /// <summary>The seven assemblies that reach nuget.org.</summary>
    /// <remarks>
    /// Named rather than discovered, the way <c>FidelityLedger.Sources</c> is, so that adding a
    /// package is a deliberate edit here. A test holds this list to the set of projects under
    /// <c>src/</c> that are packable, which is the same set <c>ci.yml</c> asserts before a push.
    /// </remarks>
    internal static Assembly[] Published =>
    [
        typeof(Abstractions.FidelityTier).Assembly,
        typeof(Client.StubIdClient).Assembly,
        typeof(InProcess.StubIdHost).Assembly,
        typeof(Profiles.IBrokerProfile).Assembly,
        typeof(Server.BrokerState).Assembly,
        typeof(Testing.StubIdBuilder).Assembly,
        typeof(Wire.Pkce).Assembly,
    ];

    /// <summary>The version these assemblies were built as.</summary>
    /// <remarks>
    /// Read off the shipped assembly rather than out of the property file, for the reason
    /// <c>VersionTests</c> gives: reading the property would only prove somebody typed it there.
    /// </remarks>
    internal static string Version() =>
        typeof(Testing.StubIdBuilder).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion.Split('+')[0];

    /// <summary>What a C# reader calls these, which is not what the runtime calls them.</summary>
    /// <remarks>
    /// The file is read by people, and <c>System.String</c> down eight hundred lines is noise a
    /// reviewer has to see past to find the change. The alias is the name the signature was
    /// written with.
    /// </remarks>
    private static readonly Dictionary<Type, string> Keywords = new()
    {
        [typeof(bool)] = "bool", [typeof(byte)] = "byte", [typeof(sbyte)] = "sbyte",
        [typeof(char)] = "char", [typeof(decimal)] = "decimal", [typeof(double)] = "double",
        [typeof(float)] = "float", [typeof(int)] = "int", [typeof(uint)] = "uint",
        [typeof(long)] = "long", [typeof(ulong)] = "ulong", [typeof(short)] = "short",
        [typeof(ushort)] = "ushort", [typeof(object)] = "object", [typeof(string)] = "string",
        [typeof(void)] = "void",
    };

    private const BindingFlags Declared =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
        | BindingFlags.Static | BindingFlags.DeclaredOnly;

    /// <summary>Every public and protected declaration, in an order that does not move.</summary>
    internal static string Compose()
    {
        var lines = new List<string>();

        foreach (var assembly in Published.OrderBy(a => a.GetName().Name, StringComparer.Ordinal))
        {
            lines.Add($"# {assembly.GetName().Name}");
            lines.Add("");

            foreach (var type in assembly.GetTypes()
                         .Where(Visible)
                         .OrderBy(Render, StringComparer.Ordinal))
            {
                lines.Add(Declaration(type));
                lines.AddRange(Members(type)
                    .OrderBy(member => member.Key, StringComparer.Ordinal)
                    .Select(member => member.Line));
                lines.Add("");
            }
        }

        // Never AppendLine: it writes Environment.NewLine, which made a generated file in this
        // same project pass on Linux and fail on Windows with a diff whose halves read identically.
        return string.Join('\n', lines).TrimEnd('\n') + "\n";
    }

    /// <summary>A member line with its deprecation marker removed.</summary>
    /// <remarks>
    /// What survives a release is the declaration, not the annotation on it: deprecating a member
    /// changes its line and must not read as removing it. Parameter names are deliberately kept,
    /// because renaming one breaks every caller passing it by name - which 2026.09.2 did to five
    /// signatures at once - and a rename that cannot be deprecated is exactly the change that
    /// should have to be written down as an exemption rather than pass unnoticed.
    /// </remarks>
    internal static string Identity(string line) =>
        line.StartsWith("  [Obsolete", StringComparison.Ordinal)
            ? "  " + line[(line.IndexOf(']') + 2)..]
            : line;

    internal static bool IsDeprecated(string line) =>
        line.StartsWith("  [Obsolete", StringComparison.Ordinal);

    internal static bool IsMember(string line) =>
        line.StartsWith("  ", StringComparison.Ordinal);

    private static bool Visible(Type type) =>
        type.IsVisible
        || (type.IsNested
            && (type.IsNestedFamily || type.IsNestedFamORAssem)
            && Visible(type.DeclaringType!));

    private static string Render(Type type) => Render(type, nullability: null, write: false);

    /// <summary>A type as C# spells it, with reference nullability at every depth it applies.</summary>
    /// <remarks>
    /// The annotation travels beside the type rather than in it, so it arrives as a parallel tree
    /// from <see cref="NullabilityInfoContext" /> and is walked in step: an argument's state comes
    /// from the matching branch of that tree, never from the type. Reading only the outermost
    /// state - which is what this did first - renders <c>Task&lt;Foo?&gt;</c> as
    /// <c>Task&lt;Foo&gt;</c>, so dropping the annotation from a returned element would have been
    /// a change the baseline could not show.
    /// </remarks>
    private static string Render(Type type, NullabilityInfo? nullability, bool write)
    {
        // A value type carries its own "?" through Nullable.GetUnderlyingType below, so only a
        // reference type takes one from here.
        var mark = nullability is not null
                   && !type.IsValueType
                   && (write ? nullability.WriteState : nullability.ReadState)
                       == NullabilityState.Nullable
            ? "?"
            : "";

        if (type.IsGenericParameter)
        {
            return type.Name + mark;
        }

        if (type.HasElementType)
        {
            var element = Render(type.GetElementType()!, nullability?.ElementType, write);

            // A by-ref or pointer type is not itself nullable - what it refers to is, and that
            // is the element rendered above.
            return type.IsArray
                ? element + "[" + new string(',', type.GetArrayRank() - 1) + "]" + mark
                : element + (type.IsByRef ? "&" : "*");
        }

        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            return Render(underlying) + "?";
        }

        if (Keywords.TryGetValue(type, out var keyword))
        {
            return keyword + mark;
        }

        var name = type.Name;
        var arity = name.IndexOf('`', StringComparison.Ordinal);

        if (arity >= 0)
        {
            name = name[..arity];
        }

        var prefix = type.IsNested
            ? Render(type.DeclaringType!) + "+"
            : string.IsNullOrEmpty(type.Namespace) ? "" : type.Namespace + ".";

        var arguments = type.IsGenericType ? type.GetGenericArguments() : [];
        var annotations = nullability?.GenericTypeArguments;

        return arguments.Length == 0
            ? prefix + name + mark
            : prefix + name + "<"
              + string.Join(", ", arguments.Select((argument, index) => Render(
                  argument,
                  annotations is not null && index < annotations.Length ? annotations[index] : null,
                  write)))
              + ">" + mark;
    }

    private static string Declaration(Type type)
    {
        var access = !type.IsNested || type.IsNestedPublic ? "public" : "protected";
        var words = new List<string> { "type", access };

        if (type.IsEnum)
        {
            words.Add("enum");
        }
        else if (type.IsInterface)
        {
            words.Add("interface");
        }
        else
        {
            if (type.IsAbstract && type.IsSealed)
            {
                words.Add("static");
            }
            else
            {
                if (type.IsAbstract)
                {
                    words.Add("abstract");
                }

                if (type.IsSealed && !type.IsValueType)
                {
                    words.Add("sealed");
                }
            }

            if (IsRecord(type))
            {
                // "record" alone for a reference type, which is how it is declared; a value type
                // spells out "record struct" and the distinction matters to a caller.
                words.Add(type.IsValueType ? "record struct" : "record");
            }
            else
            {
                words.Add(type.IsValueType ? "struct" : "class");
            }
        }

        words.Add(Render(type));

        var bases = new List<string>();

        if (type.IsEnum)
        {
            bases.Add(Render(type.GetEnumUnderlyingType()));
        }
        else
        {
            if (type.BaseType is { } parent
                && parent != typeof(object)
                && parent != typeof(ValueType))
            {
                bases.Add(Render(parent));
            }

            // What this type adds, rather than what it inherits: an interface appearing on a base
            // class in some future version of a dependency is that dependency's change, not ours.
            var inherited = type.BaseType?.GetInterfaces() ?? [];

            bases.AddRange(type.GetInterfaces()
                .Except(inherited)
                .Select(Render)
                .OrderBy(name => name, StringComparer.Ordinal));
        }

        return string.Join(' ', words) + (bases.Count == 0 ? "" : " : " + string.Join(", ", bases));
    }

    private static bool IsRecord(Type type) =>
        type.GetMethod("<Clone>$", BindingFlags.Public | BindingFlags.Instance) is not null
        || type.GetMethod("PrintMembers", Declared) is not null;

    private static IEnumerable<(string Key, string Line)> Members(Type type)
    {
        if (type.IsEnum)
        {
            foreach (var value in type.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                var literal = Convert.ChangeType(
                    value.GetValue(null)!,
                    type.GetEnumUnderlyingType(),
                    System.Globalization.CultureInfo.InvariantCulture);

                yield return ($"{literal:D20}", $"  enum {value.Name} = {literal}");
            }

            yield break;
        }

        var record = IsRecord(type);
        var nullability = new NullabilityInfoContext();

        foreach (var member in type.GetMembers(Declared))
        {
            if (!Wanted(member, record))
            {
                continue;
            }

            var line = member switch
            {
                ConstructorInfo ctor => "ctor " + Parameters(ctor, nullability),
                MethodInfo method =>
                    $"method {Modifiers(method)}{Returned(method, nullability)} "
                    + method.Name + Generics(method) + " " + Parameters(method, nullability),
                PropertyInfo property =>
                    $"property {Required(property)}{Annotated(property, nullability)} "
                    + property.Name + " " + Accessors(property),
                FieldInfo field => "field " + Field(field, nullability),
                EventInfo declared =>
                    $"event {Render(declared.EventHandlerType!)} {declared.Name}",
                _ => null,
            };

            if (line is not null)
            {
                // Sorted by kind, then by the member's own name, then by the whole line. Sorting
                // on the line alone would file properties under the type they return rather than
                // under what they are called, which is not how anybody looks for one.
                var kind = line[..line.IndexOf(' ', StringComparison.Ordinal)];

                yield return ($"{kind}|{member.Name}|{line}", "  " + Deprecation(member) + line);
            }
        }
    }

    private static bool Wanted(MemberInfo member, bool record)
    {
        if (member.Name.Contains('<', StringComparison.Ordinal)
            || member.GetCustomAttribute<CompilerGeneratedAttribute>() is not null
            // Scaffolding rather than surface. A record with a required member gets a
            // parameterless constructor carrying this and an [Obsolete] telling an old compiler
            // to keep away - rendering it would put a deprecation in the file that nobody wrote
            // and that no release is ever going to remove.
            || member.GetCustomAttribute<CompilerFeatureRequiredAttribute>() is not null)
        {
            return false;
        }

        // A record's synthesized members are the same on every record and say nothing about this
        // one. Roslyn does not mark all of them CompilerGenerated, so they go by name as well.
        if (record && member.Name is "Equals" or "GetHashCode" or "ToString" or "PrintMembers"
                or "Deconstruct" or "EqualityContract" or "Clone")
        {
            return false;
        }

        return member switch
        {
            ConstructorInfo ctor => Reachable(ctor),
            // IsSpecialName also covers operators, which this therefore does not render.
            MethodInfo method => Reachable(method) && !method.IsSpecialName,
            PropertyInfo property =>
                (property.GetMethod is { } get && Reachable(get))
                || (property.SetMethod is { } set && Reachable(set)),
            FieldInfo field => field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly,
            EventInfo declared => declared.AddMethod is { } add && Reachable(add),
            // Nested types are rendered as types in their own right by the sweep in Compose,
            // so they are not repeated here as members of the type declaring them.
            _ => false,
        };
    }

    private static bool Reachable(MethodBase method) =>
        method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly;

    private static string Deprecation(MemberInfo member)
    {
        if (member.GetCustomAttribute<ObsoleteAttribute>() is not { } obsolete)
        {
            return "";
        }

        var identifier = obsolete.DiagnosticId is { Length: > 0 } id ? " " + id : "";

        return $"[Obsolete{identifier}] ";
    }

    private static string Modifiers(MethodInfo method) =>
        method.IsStatic ? "static "
        : method.IsAbstract ? "abstract "
        : method.IsVirtual && !method.IsFinal ? "virtual "
        : "";

    private static string Required(PropertyInfo property) =>
        property.GetCustomAttributes().Any(a => a.GetType().Name == "RequiredMemberAttribute")
            ? "required " : "";

    private static string Generics(MethodInfo method) =>
        method.IsGenericMethodDefinition
            ? "<" + string.Join(", ", method.GetGenericArguments().Select(Render)) + ">"
            : "";

    private static string Accessors(PropertyInfo property)
    {
        var parts = new List<string>();

        if (property.GetMethod is { } get && Reachable(get))
        {
            parts.Add("get;");
        }

        if (property.SetMethod is { } set && Reachable(set))
        {
            // An init-only setter carries a modreq the compiler reads; without it, replacing init
            // with set looks identical here and is a real change to how a caller may build one.
            parts.Add(set.ReturnParameter.GetRequiredCustomModifiers()
                .Any(m => m.Name == "IsExternalInit") ? "init;" : "set;");
        }

        return "{ " + string.Join(' ', parts) + " }";
    }

    private static string Field(FieldInfo field, NullabilityInfoContext nullability)
    {
        var kind = field.IsLiteral ? "const " : field.IsStatic ? "static " : "";
        var value = field.IsLiteral ? " = " + Literal(field.GetRawConstantValue()) : "";

        return kind + Annotated(field, nullability) + " " + field.Name + value;
    }

    private static string Parameters(MethodBase method, NullabilityInfoContext nullability) =>
        "(" + string.Join(", ", method.GetParameters().Select(parameter =>
            Modifier(parameter)
            + Annotated(parameter, nullability)
            + " " + parameter.Name
            + (parameter.HasDefaultValue ? " = " + Default(parameter) : ""))) + ")";

    private static string Modifier(ParameterInfo parameter) =>
        parameter.IsOut ? "out " : parameter.ParameterType.IsByRef ? "ref " : "";

    private static string Default(ParameterInfo parameter) =>
        // Reflection reports default(SomeStruct) as null, which is not what a caller writes and
        // not what it means: a CancellationToken defaulting to "null" would read as a nullable one.
        parameter.DefaultValue is null
        && parameter.ParameterType.IsValueType
        && Nullable.GetUnderlyingType(parameter.ParameterType) is null
            ? "default"
            : Literal(parameter.DefaultValue);

    /// <summary>A constant as it would be written, so a changed value reads as a changed value.</summary>
    /// <remarks>
    /// The value of a public const is part of the surface rather than an implementation detail:
    /// it is baked into whatever compiled against it, so a consumer keeps the old one until they
    /// rebuild. <c>StubIdBuilder.StubIdImage</c> is the one that moves here every release.
    /// </remarks>
    private static string Literal(object? value) =>
        value switch
        {
            null => "null",
            string text => $"\"{text}\"",
            bool flag => flag ? "true" : "false",
            var other => Convert.ToString(other, System.Globalization.CultureInfo.InvariantCulture)
                ?? "",
        };

    // Reference-type nullability lives in attributes rather than in the type, so it is read
    // through NullabilityInfoContext and rendered beside it. The context is not thread-safe and
    // xUnit runs classes in parallel, so one is made per composition and never shared.
    private static string Returned(MethodInfo method, NullabilityInfoContext nullability) =>
        Render(method.ReturnType, nullability.Create(method.ReturnParameter), write: false);

    // A parameter is written to, so its write state is the one a caller has to satisfy.
    private static string Annotated(ParameterInfo parameter, NullabilityInfoContext nullability) =>
        Render(parameter.ParameterType, nullability.Create(parameter), write: true);

    private static string Annotated(PropertyInfo property, NullabilityInfoContext nullability) =>
        Render(property.PropertyType, nullability.Create(property), write: false);

    private static string Annotated(FieldInfo field, NullabilityInfoContext nullability) =>
        Render(field.FieldType, nullability.Create(field), write: false);
}
