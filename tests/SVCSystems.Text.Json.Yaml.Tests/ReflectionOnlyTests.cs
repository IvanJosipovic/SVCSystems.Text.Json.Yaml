using System.Runtime.CompilerServices;

namespace SVCSystems.Text.Json.Yaml.Tests;

/// <summary>
/// The reflection based <see cref="YamlSerializer"/> overloads require unrestricted reflection and
/// are annotated with <c>RequiresUnreferencedCode</c>/<c>RequiresDynamicCode</c>. They cannot run
/// in the Native AOT test suite (published with
/// <c>JsonSerializerIsReflectionEnabledByDefault=false</c>), so tests exercising them are
/// dynamically skipped there.
/// </summary>
internal static class ReflectionOnlyTests
{
    public static bool ReflectionAvailable => RuntimeFeature.IsDynamicCodeSupported;

    /// <summary>
    /// Skips the current test when reflection based JSON serialization is not available.
    /// Must be called before any other test logic.
    /// </summary>
    public static void SkipWhenReflectionUnavailable()
    {
        if (!ReflectionAvailable)
        {
            Xunit.Assert.Skip("Reflection based JSON serialization is unavailable in Native AOT. Use the JsonTypeInfo<T> overloads instead.");
        }
    }
}
