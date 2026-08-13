using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GisServer.Geometry;
using Xunit;

namespace GisServer.Architecture.Tests;

/// <summary>
/// Enforces <c>docs/build-vs-adopt-policy.md</c> §4 as a build failure rather
/// than as a policy nobody checks.
/// </summary>
/// <remarks>
/// <para>
/// The policy says Tier 1 — the server domain — is written by us, always; Tier 2
/// libraries are permitted but only behind our own port; and <b>no library type
/// may appear in a Tier 1 signature</b>.
/// </para>
/// <para>
/// Independent review 3 found that this project's rules tend to hold until they
/// are inconvenient, and that a rule enforced only by reading is a rule that
/// fails under pressure. This test is the pressure-independent version.
/// </para>
/// </remarks>
public sealed class TierBoundaryTests
{
    /// <summary>
    /// Assemblies a Tier 1 project may reference: the base class library, and
    /// nothing else. Deliberately a strict allow-list rather than a deny-list of
    /// known offenders — a deny-list only catches the dependency you already
    /// thought of.
    /// </summary>
    private static readonly string[] PermittedReferencePrefixes =
    [
        "System",
        "netstandard",
        "mscorlib",
        "Microsoft.CSharp",
        "Microsoft.VisualBasic",
        "WindowsBase",
    ];

    private static Assembly CoreAssembly => typeof(XySequence).Assembly;

    [Fact]
    public void Core_references_nothing_outside_the_base_class_library()
    {
        List<string> offenders = CoreAssembly
            .GetReferencedAssemblies()
            .Select(static a => a.Name ?? string.Empty)
            .Where(static name => !IsPermitted(name))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"""
             {CoreAssembly.GetName().Name} is Tier 1 and must reference nothing outside the
             base class library. It references:

                 {string.Join("\n    ", offenders)}

             build-vs-adopt-policy.md §4: Tier 2 libraries are permitted, but only behind
             our own port interface, in an adapter project. If this dependency is genuinely
             needed, the port belongs in Tier 1 and the reference belongs in the adapter.
             """);
    }

    [Fact]
    public void Core_exposes_no_type_from_outside_the_base_class_library()
    {
        // The reference test above is the strong one today, because Core has no
        // package references at all. This one survives the day Core legitimately
        // gains one — a port's own dependency, say — and still has to keep
        // library types out of its public signatures.
        List<string> offenders = [];

        foreach (Type type in CoreAssembly.GetExportedTypes())
        {
            foreach (MethodInfo method in type.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                Check(method.ReturnType, $"{type.Name}.{method.Name} return");
                foreach (ParameterInfo parameter in method.GetParameters())
                {
                    Check(parameter.ParameterType, $"{type.Name}.{method.Name}({parameter.Name})");
                }
            }

            foreach (PropertyInfo property in type.GetProperties(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                Check(property.PropertyType, $"{type.Name}.{property.Name}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"""
             A Tier 1 signature exposes a type from outside the base class library:

                 {string.Join("\n    ", offenders)}

             build-vs-adopt-policy.md §4: no library type may appear in a Tier 1 signature.
             Wrap it in a type we own.
             """);

        void Check(Type type, string where)
        {
            Type target = type.IsByRef || type.IsArray || type.IsPointer
                ? type.GetElementType() ?? type
                : type;

            if (target.IsGenericType)
            {
                foreach (Type argument in target.GetGenericArguments())
                {
                    Check(argument, where);
                }

                target = target.GetGenericTypeDefinition();
            }

            string? name = target.Assembly.GetName().Name;
            if (name is null || IsPermitted(name) || target.Assembly == CoreAssembly)
            {
                return;
            }

            offenders.Add($"{where} is {target.FullName} from {name}");
        }
    }

    private static bool IsPermitted(string assemblyName) =>
        PermittedReferencePrefixes.Any(prefix =>
            assemblyName.Equals(prefix, StringComparison.Ordinal) ||
            assemblyName.StartsWith(prefix + ".", StringComparison.Ordinal));
}
