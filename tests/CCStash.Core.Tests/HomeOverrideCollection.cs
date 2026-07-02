namespace CCStash.Core.Tests;

/// <summary>
/// Collection definition that forces all test classes mutating the process-global
/// <c>CCSTASH_HOME_OVERRIDE</c> environment variable to run serially, since xUnit
/// otherwise parallelizes test classes within the same assembly and the shared
/// mutable state would race.
/// </summary>
[CollectionDefinition("CCSTASH_HOME_OVERRIDE serial", DisableParallelization = true)]
public class HomeOverrideCollection;
