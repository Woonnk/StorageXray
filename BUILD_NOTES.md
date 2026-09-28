# Build notes for 0.2

The included `Build-Windows.cmd` is the normal build path: the .NET 10 SDK restores its Windows targeting/runtime packs, runs the core tests, and publishes a self-contained Windows x64 executable.

This release was built in an offline Linux environment. The original saved 0.1 package was validated and its source matched the working source before modification. NuGet was unreachable in that environment.

The core and filesystem test runner were built with the available .NET 10 SDK and its normal .NET reference assemblies. The WPF app and XAML were compiled against matching Windows .NET/WPF implementation assemblies from the previous self-contained package, with implicit framework references disabled. The existing self-contained runtime and apphost were retained; no runtime upgrade is claimed. The host's native resources were updated from the new 0.2 assembly using the SDK ResourceUpdater, and the SDK's `Microsoft.NET.HostModel.Bundle.Bundler` generated the new compressed Windows x64 bundle. The dependency manifest was updated to app version 0.2.0.

Bundle verification decompressed all entries and compared them with the staged source bytes, including the new app and core binaries. This verifies packaging, not Windows execution. Native Windows testing remains outstanding.

The source uses no NuGet package dependency for SQLite: it loads `winsqlite3.dll` from the Windows system directory, or `libsqlite3.so.0` for Linux core tests. Parameter binding is used for paths and filters. No source file contents are stored in the inventory database.

## 0.2.1 patch

Only application dropdown styling and displayed/version metadata changed. The app and XAML were rebuilt with zero warnings/errors; the core binary is unchanged. The host resources and app dependency manifest now identify 0.2.1. Bundle verification is repeated for the patch.
