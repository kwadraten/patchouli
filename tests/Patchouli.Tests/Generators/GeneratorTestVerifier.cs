using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Microsoft.CodeAnalysis.Testing;
using Xunit.Sdk;

namespace Patchouli.Tests.Generators;

/// <summary>
/// Verifier for the Roslyn source-generator test framework.
/// <para>
/// <c>Microsoft.CodeAnalysis.CSharp.SourceGenerators.Testing.XUnit</c> 1.1.2 ships an
/// <c>XUnitVerifier</c> that constructs <c>Xunit.Sdk.EqualException(object, object)</c>, a
/// constructor removed in xunit 2.9.3. Any failed assertion therefore surfaced as a
/// <see cref="MissingMethodException"/> instead of the real diagnostic diff.
/// </para>
/// <para>
/// This verifier implements <see cref="IVerifier"/> directly and reports failures via
/// <see cref="XunitException"/>, which is public in xunit 2.9.3.
/// </para>
/// </summary>
public sealed class GeneratorTestVerifier : IVerifier
{
    public void Empty<T>(string collectionName, IEnumerable<T> collection)
    {
        List<T> items = collection.ToList();
        if (items.Count != 0)
        {
            throw new XunitException(
                $"Expected collection '{collectionName}' to be empty, but it contained {items.Count} item(s):{Environment.NewLine}{Format(items)}");
        }
    }

    public void NotEmpty<T>(string collectionName, IEnumerable<T> collection)
    {
        List<T> items = collection.ToList();
        if (items.Count == 0)
        {
            throw new XunitException($"Expected collection '{collectionName}' to be non-empty, but it was empty.");
        }
    }

    public void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new XunitException(Format(message,
                $"Assert.Equal() Failure{Environment.NewLine}Expected: {Format(expected)}{Environment.NewLine}Actual:   {Format(actual)}"));
        }
    }

    public void True(bool assert, string? message = null)
    {
        if (!assert)
        {
            throw new XunitException(Format(message,
                "Assert.True() Failure: expected condition to be true, but it was false."));
        }
    }

    public void False(bool assert, string? message = null)
    {
        if (assert)
        {
            throw new XunitException(Format(message,
                "Assert.False() Failure: expected condition to be false, but it was true."));
        }
    }

    [DoesNotReturn]
    public void Fail(string? message = null)
    {
        throw new XunitException(Format(message, "Assert.Fail() Failure."));
    }

    public void LanguageIsSupported(string language)
    {
        if (string.IsNullOrEmpty(language))
        {
            throw new XunitException("Assert.LanguageIsSupported() Failure: the language must be non-empty.");
        }
    }

    public void SequenceEqual<T>(
        IEnumerable<T> expected,
        IEnumerable<T> actual,
        IEqualityComparer<T>? equalityComparer = null,
        string? message = null)
    {
        List<T> expectedItems = expected.ToList();
        List<T> actualItems = actual.ToList();
        IEqualityComparer<T> comparer = equalityComparer ?? EqualityComparer<T>.Default;

        if (!expectedItems.SequenceEqual(actualItems, comparer))
        {
            throw new XunitException(Format(
                message,
                $"Assert.SequenceEqual() Failure{Environment.NewLine}Expected: {Format(expectedItems)}{Environment.NewLine}Actual:   {Format(actualItems)}"));
        }
    }

    public IVerifier PushContext(string context)
    {
        return this;
    }

    private static string Format(string? message, string failure)
    {
        return message is null ? failure : $"{message}{Environment.NewLine}{failure}";
    }

    private static string Format<T>(T value)
    {
        return value?.ToString() ?? "null";
    }

    private static string Format<T>(IEnumerable<T> values)
    {
        return "[" + string.Join(", ", values.Select(Format)) + "]";
    }
}
