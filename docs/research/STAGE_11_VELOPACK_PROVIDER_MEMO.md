# Stage 11 Velopack provider-admission memo

## Decision

`PROPOSED`: Velopack remains a plausible future Windows installer/updater provider, but Stage 11
does **not** add it to the production dependency graph. The current dependency-free,
self-contained, immutable side-by-side bundle remains the accepted local RC mechanism.

Changing this decision requires an owner-approved `docs/DECISIONS.md` entry and a separate scope
covering signing, package provenance, install/uninstall ownership, offline media, downgrade,
antivirus and clean-VM acceptance.

## Bounded source reviewed

- upstream: `https://github.com/velopack/velopack.git`;
- exact commit: `214f8be7384201826ba03d4bad8f0f646d727613`;
- local pinned mirror: `C:\Projects\Map-ResearchLab\sources\velopack`;
- license: MIT, copyright 2021 Caelan Sayler and 2024 Velopack Ltd.;
- files: `LICENSE`, `samples/CSharpAvalonia/README.md`, `Program.cs`, `MainWindow.axaml`,
  `MainWindow.axaml.cs`, `MemoryLogger.cs`, the sample project file,
  `src/lib-csharp/VelopackApp.cs`, `UpdateManager.cs`,
  `sources/SimpleFileSource.cs`, and the narrow related UpdateManager tests.

The source checkout was clean at the pinned commit and was read-only. No upstream code, package,
asset, name or visual design was copied into Map.

## What the source establishes

- The Avalonia sample calls `VelopackApp.Build().Run()` at the beginning of `Main`, before normal
  Avalonia startup. Integration therefore changes an early process boundary, not only a settings
  screen.
- `VelopackApp` auto-applies a newer already-downloaded local package on startup by default;
  `SetAutoApplyOnStartup(false)` is required to opt out. It also handles lifecycle arguments,
  may set a Windows AUMID, invokes a separate update executable and removes older local packages.
- `UpdateManager` accepts an explicit source, URL or file path. `SimpleFileSource` can read a local
  release index or enumerate local `.nupkg` files, so a local/offline distribution topology is
  possible without making a cloud service an application prerequisite.
- Downgrade is opt-in through `AllowVersionDowngrade`; the default is false. Download/apply uses a
  global update lock, staged packages, integrity metadata and a separate apply/restart process.
- Cleanup and apply operations write/delete provider-owned files and wait for processes. Those
  operations need exact ownership and rollback tests before adoption.

These are API/source observations at the pinned commit. They are not proof that a Map installer is
offline-safe, downgrade-safe or removable without data loss.

## Admission gaps

| Question | Current answer |
|---|---|
| Can Map launch/edit/save offline? | The present portable bundle can; a Velopack-installed build has not been tested. |
| Is local-media update possible? | Source supports a file source, but Map has no accepted feed/package/signing contract. |
| Is downgrade automatic? | No. It is an explicit option and still needs Map schema/data compatibility gates. |
| Is rollback data-safe? | Not demonstrated. Provider package rollback cannot replace project-copy and schema rules. |
| Does uninstall preserve maps/settings? | Not demonstrated for Map. Ownership of every installed and user-data path is undefined. |
| Are packages signed and antivirus-clean? | Not demonstrated; there is no accepted signing identity or release pipeline. |
| Does it preserve Map's AUMID/icon identity? | Possible to configure, but not integrated or tested. |
| Does it require cloud? | The library offers local sources, but the convenient default constructor uses Velopack Flow; Map must never rely on that default. |

## Minimum future plan

1. Record an owner decision naming provider, package/source topology, AUMID, signing identity,
   install scope, owned paths, network policy and downgrade rules.
2. Pin an admitted package version and reproduce its full direct/transitive license and
   vulnerability inventory.
3. Build signed packages from a clean commit in an isolated release task. Disable implicit startup
   apply until the owner accepts explicit behavior.
4. On fresh offline Windows VMs, test install, first run, update from local media, interrupted
   apply, old/new side-by-side project copies, downgrade, uninstall and settings/project survival.
5. Keep the portable bundle and documented side-by-side rollback until those gates pass.

Stage 11 therefore extracts provider risks and a future test plan only. Production integration,
installer behavior and automatic update remain outside this implementation slice.
