# Portable projects and recovery

`Mosaic.Storage` implements product requirements R-16 and §9. A `.mosaic` file is a ZIP with a versioned JSON manifest, original downloaded image bytes and a small preview:

```text
manifest.json
source/original.png       # or .jpg/.jpeg/.webp/.bmp, unchanged original bytes
assets/thumbnail.image    # optional encoded preview
```

The manifest stores exact decimal inch dimensions, dimension lock and last edited dimension, composition and rotation, image quality/model recipe, paper/margins, Auto/manual orientation and resolved orientation snapshot, printer reference, copies, color policy and assembly-guide choice. Schema 1 is the first supported schema. Unknown future schemas are rejected with a dedicated error; no silent migration or reinterpretation occurs.

`ProjectStore.SaveAsync` streams the source into a new archive, computes SHA-256 while copying, and checks it against immutable source identity. It calculates the saved orientation snapshot using the common `LayoutEngine`. A successfully closed archive atomically replaces the destination on the same volume. Cancellation or a failed write removes the temporary file and preserves any previously saved project. It never reads only an external image reference.

`PeekAsync` reads only the bounded manifest and thumbnail, without extracting or decoding the source. `OpenAsync` validates the archive and extracts source bytes to a hash-named cache file. It verifies SHA-256 before publishing the file; image decoding remains the imaging module's responsibility. The source therefore survives moving or deleting the original download.

Safety bounds are 256 MiB per source, 16 MiB per preview, 128 KiB per manifest, and three archive entries. Duplicate names, unexpected entries, path traversal, future schema, extreme compression ratios, invalid enums, invalid composition, and invalid physical geometry are rejected. The copier enforces actual byte limits in addition to ZIP metadata limits. Extraction targets are fixed application-generated filenames, never archive-provided paths. Invalid input cannot overwrite the user's project or extract files outside the cache.

`PreferencesStore` stores `settings.json` under `%LocalAppData%\MosaicTemplateStudio` by default. It persists English/Polish preference, eight recent projects, the dismissible calibration prompt, and up to 32 printer/path-specific calibration records. Settings writes use an atomic sibling temporary file. Damaged settings are retained as `settings.json.damaged` before usable defaults are returned. User file paths are only local settings; no telemetry or remote synchronization exists.

`RecoveryPath` is `%LocalAppData%\MosaicTemplateStudio\recovery.mosaic`. The editor uses the same self-contained save contract for periodic recovery. Recovery data is independent of an explicit Save As destination and must be retained when shutdown is interrupted. Reopening a recovered project creates editable state without replacing a personal project until an explicit save.

The extracted project cache is disposable and bounded to 2 GiB; source extraction also requires 128 MiB of free-disk headroom beyond the expected extraction size. A full cache produces a recoverable `StorageFull` error instead of growing indefinitely. `ClearCacheAsync` removes only top-level 64-hex-SHA-named source and preview files, checks absolute paths stay inside its cache root, refuses a reparse-point root and skips linked files. It preserves all `.mosaic` files and unknown files. Cleanup is cancellable and leaves in-use assets for a later attempt. The application does not embed derived AI caches inside projects; enhancement can be regenerated from immutable source and the saved recipe. Uninstall does not delete settings, recovery, caches or personal projects.

Validation: `tests/Mosaic.Storage.Checks` is a dependency-free executable covering lazy preview, Polish metadata, a 60-inch editable round trip after deleting the source download, manual orientation snapshot, changed-source rejection, malformed archives, traversal, duplicate entries, unsupported schema, invalid geometry, compression ratio limits, cancellation preserving existing files, bounded recents and settings recovery. Run with `.tools\dotnet\dotnet.exe run --project tests/Mosaic.Storage.Checks -c Release`.
