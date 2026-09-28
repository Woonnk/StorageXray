# StorageXray 0.2.1

A local Windows storage detective. Explore a drive map, find large files and exact duplicates, build a cleanup plan, and compare folder growth between scans.

## Run the app

1. Extract the entire `StorageXray-Windows-x64.zip` archive.
2. Open `StorageXray.exe` inside the extracted `StorageXray` folder.
3. Start by scanning Downloads, then try a whole drive.

The Windows x64 release includes its .NET runtime. It does not require Python, Node, an account, administrator rights, or a separate .NET installation. This release is an unsigned early build. It does not change Windows security settings or install a background service.

## Dropdown contrast fix in 0.2.1

Drive, file-type, and sort dropdowns now use explicit dark templates for both the closed control and the popup. Selected, hovered, keyboard-highlighted, and disabled states have explicit styling. XAML compiles successfully; visual confirmation on Windows remains outstanding. The 0.2 scanner core and its validation are unchanged.

## What's in this version

- **Storage map:** proportional, clickable blocks; folder navigation; contextual folder explanations.
- **Largest files:** filename/path search, category filters (including common game-library paths), global size/date/name sorting, 500-row pages, and Explorer reveal.
- **Exact duplicates:** size and sample filtering followed by full SHA-256 verification. It picks one copy to retain and lists the extra copies.
- **Cleanup planner:** set a goal in GiB, review suggested files, change the selection, and confirm the full paths in a second review window.
- **What changed:** a local baseline for each scanned root, with changes in the first three folder levels.
- **CSV export:** the scanned file inventory, with spreadsheet-formula escaping.
- **Cancellable scans:** bounded parallel top-level traversal; unreadable folder branches, links, and known cloud-sync subtrees are skipped and reported.
- **Disk-backed inventory:** file records, folder totals, duplicate fingerprints, and cleanup candidates stay in a temporary SQLite database; the UI reads bounded pages.
- **Count-based progress:** updates every 500 scanned files, plus finalization/completion updates.

The map shows the largest 79 immediate entries plus one proportional block for smaller items. All immediate entries remain accessible through the paged folder list. Files and duplicate copies are paged in batches of 500. Use the sort menu to sort all matching files; column headers do not silently sort just one page.

## How cleanup works

Nothing is selected until you choose a goal or check a file. A plan prioritizes verified duplicate copies, followed by download archives unchanged for at least 60 days and videos of at least 100 MiB unchanged for at least 90 days. Modification age is a review cue, not evidence a file is unused.

Cleanup is reviewed in pages of at most 1,000 candidates. The space goal and selected checkboxes apply to the current page. Changing pages clears its selection. If the page cannot meet the goal, the app reports the shortfall; other pages remain available. A duplicate keeper is protected across the entire inventory, including copies on other pages.

Duplicate matching and cleanup are deliberately limited to supported personal document, media, and archive types. Application folders, game installations, system files, cloud-sync folders, hidden/read-only files, and file links are excluded from cleanup. Duplicates smaller than 64 KiB are not checked. Hard-linked duplicate candidates are skipped on Windows.

Before each recycle request, the app checks the file path, attributes, size, and modification time again. For duplicates, it rehashes both copies and keeps the retained copy open against modification or deletion while Windows recycles the other one. A changed or missing copy stops that item. A plan never also suggests its retained duplicate copy.

Cleanup is limited to fixed local drives and requests Windows Shell recycling with an undo record. Its progress guard cancels an operation that is not marked for recycling. There is no permanent-delete fallback and the app never empties the Recycle Bin. Failed items are reported individually.

**Moving a file to the Recycle Bin does not immediately recover disk space.** You can restore it there. Space is recovered after you independently empty the bin in Windows. The planner reports logical file-content bytes, not guaranteed physically reclaimable space.

## Reading the numbers

StorageXray reports logical file lengths. These can differ from Windows' allocated disk usage because of hard links, sparse or compressed files, metadata, access permissions, Windows-managed storage, and cloud placeholders. Known OneDrive, Dropbox, and Google Drive subtrees are excluded before enumeration, including their locally available files. Link, offline, and recall-required attributes are also excluded. Scanning does not open file contents or deliberately hydrate placeholders. A scan can finish while some locations were inaccessible; see Scan details.

The drive free-space card comes from the volume itself. A folder's scan total is not intended to equal total used space on the drive. GiB means 1,073,741,824 bytes.

History compares completed scans of the **same root folder**. Parent and child changes overlap and should not be added together. Permission or access changes can also affect the result. Stopped scans do not replace the baseline. History capture is capped at 10,000 folders across the first three levels; larger histories are reported as unavailable and do not replace the baseline. Existing history files above 16 MiB are not loaded. This version is an on-demand scanner, not a background monitoring agent.

## Privacy and local data

The app has no networking code or telemetry. History, errors, and cleanup receipts are saved in `%LOCALAPPDATA%\StorageXray`:

- `history`: folder paths, sizes, scan times, and file counts; no file contents.
- `cleanup`: requested file paths, outcome counts, and errors.
- `errors.log`: unexpected application errors, if any.
- `scans/scan-<id>`: temporary file paths, lengths, dates, attributes, folder totals, duplicate hashes, and cleanup candidates. These contain metadata, never copies of file contents. Normal close or a replacement scan removes its index. An unexpected process termination can leave an index behind; close all StorageXray instances before manually removing abandoned `scans` folders.

CSV reports contain file paths. Delete the local StorageXray data folder if you want to remove history after closing the app. Deleting the application folder uninstalls the portable app; it does not remove scan history or touch the Recycle Bin.

## Validation and limits

The core, native WPF application, and XAML compile. Twenty-five automated filesystem and algorithm checks pass on Linux, including serial/parallel equivalence, lazy enumeration failures, ancestor and cloud exclusions, count-based progress, cancellation accounting, full-content duplicate verification, stale-file rejection, keeper protection across pages, streamed CSV export, and temporary-index disposal.

An additional **one-million-record synthetic metadata stress test** passes: a run took 11.80 seconds for insertion, indexing, paged queries, size-group preparation, and cleanup-candidate generation. Sampled process RSS peaked at 205,553,664 bytes (about 196 MiB). Retained managed-memory growth after collection was 17,904 bytes; index files occupied 437,349,856 bytes (about 417 MiB). These measurements describe this Linux test environment, not Windows filesystem scanning or duplicate hashing throughput. They are not a memory ceiling. See `TEST_RESULTS.txt` and `AUDIT_CHANGES.md`.

**The native Windows interface, Windows system SQLite loader, and Windows Shell recycle operation were not executed in the build environment.** This remains an unsigned early build. Try a small folder of disposable copies before using cleanup on personal files. A disk-full/index-write error aborts a scan instead of silently publishing an incomplete inventory.

Only Windows x64 is packaged. Windows 10/11 system SQLite (`winsqlite3.dll`) is used from the system directory. SQLite caches are bounded and temporary sorting is configured to use disk; disk requirements scale with paths and record counts. Scanning keeps only active traversal frames and a bounded work batch, not an all-file or all-folder list. Up to four top-level folders are scanned concurrently; very skewed trees or a mechanical drive may see less benefit. The core's `ScanOptions` allows one to eight workers for callers.

Known folder explanations remain path-based rules. This release does not add automatic background alerts, game uninstalling, file moving, or app-leftover deletion.

## Build from the included source

Install the .NET 10 SDK, open a terminal in the repository root, and run:

```powershell
dotnet run --project tests/StorageXray.Tests/StorageXray.Tests.csproj -c Release
# Optional one-million-record metadata stress test:
dotnet run --project tests/StorageXray.Tests/StorageXray.Tests.csproj -c Release -- --stress
dotnet publish src/StorageXray.App/StorageXray.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o release/StorageXray
```

The core has no NuGet package dependencies and calls the system SQLite C API through a small parameterized wrapper. Core tests on Linux need `libsqlite3.so.0`; the Windows package uses the OS SQLite engine. The UI is native WPF, with Windows Shell interop for recycling. This repository contains the application source and core tests. The test runner uses disposable temporary fixtures; it does not invoke recycling. The symlink fixture may require Windows Developer Mode when running tests on Windows.

The included `Build-Windows.cmd` uses the standard SDK publish workflow. See `BUILD_NOTES.md` for how this release was compiled and bundled in the offline build environment.

## API references

- [Windows Presentation Foundation](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/)
- [Windows file operation flags](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifileoperation-setoperationflags)
- [Pre-delete progress callbacks](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ifileoperationprogresssink-predeleteitem)
- [Transfer source flags](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/ne-shobjidl_core-_transfer_source_flags)

Included .NET runtime components retain their respective Microsoft / .NET Foundation notices in `THIRD-PARTY-NOTICES.txt`.

- [SQLite parameter binding](https://sqlite.org/c3ref/bind_blob.html)
- [SQLite cache and temporary storage settings](https://sqlite.org/pragma.html)
- [Windows system SQLite](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/custom-versions)
