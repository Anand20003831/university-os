module UniversityOS.Tests.AssemblyInfo

open Xunit

/// Test classes run in parallel by default. The rendering suites cannot: they
/// drive process-global state on purpose — the compiled shader library, the
/// capability probe, the shared backdrop, the pipeline's cached surfaces, the
/// painters' reused paints, and the memory-pressure registry.
///
/// Left parallel, a memory-pressure test that disposes the pipeline mid-run
/// tears surfaces out from under a golden-image test rendering on another
/// thread, and freeing a native Skia object during a draw takes the whole test
/// host down rather than failing an assertion. That is exactly what happened
/// the first time these were added.
///
/// The alternative — a shared xUnit collection over just the rendering modules
/// — is finer-grained but easy to forget on the next rendering suite, and the
/// whole run is well under a minute serially. Correctness over a few seconds.
[<assembly: CollectionBehavior(DisableTestParallelization = true)>]
do ()
