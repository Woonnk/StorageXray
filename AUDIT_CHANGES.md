# StorageXray 0.2 — audit changes

| Audit item | Implemented change |
| --- | --- |
| Remove full in-memory file lists | File records and folder totals are stored in a temporary SQLite index. No `ScanResult.Files`, `FolderNode.Files`, or retained full folder tree. |
| Remove the reversed visited list | Each depth-first frame rolls its totals into its parent as the folder finishes. Cancellation drains active frames and keeps partial counts consistent. |
| Parallelize top-level folders | Up to four concurrent workers by default. The top-level work inventory is on disk and is read 64 folders at a time; there is no task per discovered folder. The core option permits 1–8 workers. |
| Cheap exclusions before filesystem calls | Cloud-name and managed-cache checks occur before enumerating entries. Cleanup eligibility rejects protected types and paths before rechecking ancestors. |
| Batch progress | Scan and duplicate progress are reported in 500-record batches, with final phase/total updates. The UI coalesces pending progress callbacks. |
| Validate before descending | The selected root's ancestors are checked, then cloud names and fresh directory attributes are checked at each traversal boundary. Links, offline/recall entries, and known cloud-sync subtrees are skipped. |
| Handle enumeration errors at folder level | Lazy enumeration and metadata errors stop that folder branch. Already counted entries remain; other branches continue and the failure is recorded. Index-write errors abort the scan rather than being swallowed as source-folder skips. |
| Page UI consumers | Files, immediate folder entries, and duplicate copies use 500-row pages. Sorting and literal Unicode path filtering run against the entire disk index. CSV export streams every record. |
| Avoid moving the memory problem elsewhere | Candidate sizes, sample fingerprints, full hashes, duplicate groups, and cleanup candidates are persisted and grouped in SQLite. No whole-inventory LINQ `GroupBy`, sort, or keeper-path set is retained. |

`.git`, application, and game folders still contribute to storage totals. Their files remain excluded from cleanup. Skipping these folders entirely would hide relevant usage. Full ancestor and content checks remain mandatory immediately before hashing/recycling; early scan checks do not replace them.

The map reads the largest 79 immediate entries and aggregates the remainder into one proportional block. The paged folder list exposes all entries. Cleanup review is bounded to 1,000 candidates per page; its goal applies to that page and changing pages clears selection. Duplicate keepers are reserved across all pages. History capture is capped at 10,000 folders within three levels and reports a limit instead of replacing a baseline with truncated data.

SQLite uses a 4 MiB suggested page cache per connection, disables mmap, and uses disk-backed temporary sorting. Writes are committed in batches. Filesystem workers share a synchronized writer; this is bounded parallel filesystem enumeration, not parallel SQLite writes. Throughput gains depend on drive hardware and tree shape. A dominant single subtree can still limit parallelism.

## Validation

- 25 functional checks passed on Linux.
- The additional one-million-record synthetic metadata test passed (26 total in that run).
- The final WPF application and XAML compiled with zero warnings and zero errors.
- Every entry in the rebuilt executable bundle was decompressed and matched to its staged input; the app and core matched the newly compiled binaries.
- Existing stale-file rejection, full SHA-256 duplicate verification, retained-copy protection, and recycle-only behavior remain in place.

Measured metadata test: 11.80 seconds; 205,553,664 bytes sampled peak process RSS; 17,904 bytes retained managed-memory growth after collection; 437,349,856 bytes of index files. This is not a Windows drive-scan benchmark, not a measurement of live peak managed memory, and not a hard memory bound. File paths and database indexes consume disk space instead of being retained as C# objects.

The Windows UI, OS SQLite loader, and Windows Shell recycling were not run here. Cleanup must still be checked on Windows using disposable copies. Index files are removed on normal close/replacement; interrupted processes can leave cache files. See README.md for data locations and limits.

## 0.2.1 dropdown fix

Explicit dark ComboBox and ComboBoxItem templates replace the default Windows white controls for all three dropdowns. Hover/selection and keyboard highlight styles remain readable against their backgrounds. This patch changes no scan or cleanup logic.
