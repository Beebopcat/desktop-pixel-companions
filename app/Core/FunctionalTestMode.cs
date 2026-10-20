namespace AzerothCompanion.Core;

/// <summary>Explicit process-only functional test. It never changes the animation review gate or preferences.</summary>
internal static class FunctionalTestMode
{
#if COMPANION_FUNCTIONAL_ONLY
    internal const bool BuildRequiresFunctionalMode = true;
#else
    internal const bool BuildRequiresFunctionalMode = false;
#endif
    internal static bool Active { get; private set; }
    internal static void Enable() => Active = true;
    internal static string[] StartupArguments(string[] arguments, bool forceFunctionalMode) =>
        forceFunctionalMode && !arguments.Contains("--functional-test", StringComparer.Ordinal)
            ? ["--functional-test", .. arguments] : arguments;
}
