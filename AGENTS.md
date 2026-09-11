# AGENTS.md

This file defines the repository-wide working rules for contributors and coding agents in CDSI Beacon for macOS.

## Scope and Sources of Truth

- These rules apply to the entire repository unless a more specific `AGENTS.md` is added below a directory.
- Read `README.md`, this file, the affected project files, and nearby tests before changing behavior.
- The root `VERSION` file is the only hand-maintained product version source.
- Treat the current code and tests as evidence of implemented behavior. Do not describe planned work as complete.
- Keep macOS behavior explicit. Do not assume that code copied from the Windows client is correct on macOS.

## Project Overview

Beacon for macOS is a local-first creator asset management application built with .NET 10 and Avalonia.

The repository is organized as follows:

- `CDSI.Agent.Core`: domain entities, value objects, policies, and interfaces.
- `CDSI.Agent.Application`: use cases and orchestration over Core abstractions.
- `CDSI.Agent.Infrastructure`: SQLite, scanning, metadata, remote services, and other adapters.
- `CDSI.Agent.Mac`: Avalonia UI, composition root, application lifecycle, and macOS integrations.
- `tests/CDSI.Agent.Core.Tests`: domain tests.
- `tests/CDSI.Agent.Infrastructure.Tests`: adapter and persistence tests.
- `tests/CDSI.Agent.IntegrationTests`: application workflow tests.
- `tests/CDSI.Agent.Mac.Tests`: macOS presentation and platform integration tests.
- `scripts`: macOS packaging and icon generation scripts.
- `Resources` and `CDSI.Agent.Mac/Assets`: source and generated application icon assets.

The supported application target is macOS 12 or later on Apple Silicon (arm64). Intel Macs, x86_64 packages, and Universal Binaries are not supported.

## Architecture Rules

- `CDSI.Agent.Core` must not depend on Avalonia, SQLite, cloud SDKs, macOS APIs, or other infrastructure details.
- `CDSI.Agent.Application` may depend on Core abstractions but must not contain UI code or provider SDK calls.
- `CDSI.Agent.Infrastructure` implements Core abstractions and must keep provider-specific behavior behind narrow interfaces.
- `CDSI.Agent.Mac` owns UI composition and macOS-specific adapters. Do not add WinForms dependencies.
- UI code must not issue raw SQL, access secrets directly, or bypass application services for business operations.
- Keep filesystem observation separate from scan and reconciliation policy.
- Register platform implementations through the composition root rather than adding operating-system branches throughout shared code.
- Add abstractions only when they enforce a real boundary, enable testing, or remove meaningful duplication.

When a shared implementation contains Windows-specific adapters, keep them isolated and ensure the macOS composition root cannot select them accidentally.

## Product Invariants

1. User data remains local by default.
2. User-selected scan roots are read-only unless the user explicitly starts a file-changing operation.
3. Scanning, searching, tagging, project membership, and saving configuration must not upload, move, rename, overwrite, publish, or delete source files.
4. Destructive and externally visible operations require a clear user action and an accurate confirmation step.
5. Beacon preserves the user's existing directory organization and does not silently reorganize it.
6. A failed or cancelled network operation must not corrupt local state or report success.
7. Missing or unavailable file locations must not silently delete the logical asset record.
8. Do not introduce telemetry, analytics, or background uploads without an explicit product decision and opt-in design.

Treat deleting a local project, removing an asset from a project, hiding an asset record, deleting a local file, and deleting a cloud copy as separate operations. None implies another.

## Filesystem and macOS Behavior

- Access only paths selected or otherwise explicitly authorized by the user.
- Respect macOS privacy controls. Do not request Full Disk Access as a workaround for ordinary permission or ownership problems.
- Do not assume Windows drive letters, separators, case rules, or Volume GUID behavior.
- Account for case-sensitive and case-insensitive volumes, Unicode normalization, aliases, symbolic links, mount points, packages, hidden files, network volumes, and unavailable iCloud placeholders.
- Prevent recursive scan loops through symbolic links or repeated mount paths.
- Preserve the user's original display path even when a normalized value is used for comparison.
- Identify removable storage by stable volume identity where possible, not only by mount path.
- File identity must not be equivalent to the current path. A logical asset can move or have multiple locations.
- SHA-256 may be deferred for large files; a missing checksum is a valid state and should be displayed accordingly.
- Long scans, hashes, copies, uploads, and restores must support cancellation and bounded concurrency.

Never execute files, scripts, macros, or embedded content merely because they were discovered during a scan.

## Data and Persistence

- Normal application data defaults to `~/Library/Application Support/CDSI`.
- `CDSI_BEACON_DATA_DIRECTORY` is for isolated automation and testing; use an absolute disposable directory.
- Tests must never operate on the developer's real Beacon database, workspace, home directory, or asset library.
- Use stable UUIDs for the client installation, assets, projects, and remote records.
- Keep asset identity separate from file locations and display names.
- SQLite schema changes require ordered, versioned, transactional migrations and migration tests.
- Back up databases with SQLite-safe snapshot or backup APIs. Do not treat copying only an active main database file as a valid backup when WAL data may exist.
- Restore through staging, validation, atomic replacement, and a recoverable rollback path.
- Batch related writes in transactions and avoid loading an entire large library into memory.
- Keep `client-identity.json` independent from SQLite recovery. Restoring a database must not silently replace the target installation identity.
- Never share a live SQLite database between macOS and Windows clients.

Cross-client exchange requires a documented, versioned contract. Similar class, table, or property names are not proof of compatibility. Cloud project manifests and object keys must use stable identifiers and explicit conflict behavior before cross-device synchronization relies on them.

## Secrets and Privacy

- Store object-storage secrets, WordPress application passwords, Git passwords, and similar credentials in macOS Keychain under the service `com.cdsi.beacon`.
- SQLite and preferences may store non-secret configuration and opaque Keychain references only.
- Never commit, log, export, or include in ordinary backups any real password, access token, refresh token, cookie, private key, signed URL, browser profile, or signing identity.
- Redact authorization headers, URL query secrets, and provider error payloads before logging.
- Do not read, copy, upload, or overwrite a user's SSH private key.
- Treat embedded browser sessions as credentials. Isolate them by site and account, and exclude them from normal state backups.
- Prefer official APIs and external-browser authentication. An embedded browser must not be presented as bypassing login, MFA, CAPTCHA, or platform policy.
- Logs should retain enough context for diagnosis while minimizing exposure of personal paths and filenames.

## Networking, Backup, and Publishing

- Saving a connection profile must not upload data.
- Remote writes must be explicitly initiated, cancellable, and idempotent where practical.
- Record provider-specific backup state independently when a project uses multiple providers.
- Upload and verify project resources before committing a remote project manifest.
- Never overwrite or merge same-name remote projects merely by comparing display names; use stable project identity and an explicit conflict decision.
- Report partial success precisely. Do not mark a project fully backed up when any required object or manifest failed.
- OpenWeb publication must use stable remote post identifiers to avoid duplicate publishing.
- Network tests must use fakes, mocks, or controlled local endpoints and must not contact production services by default.

## Avalonia UI Rules

- Follow macOS expectations for the menu bar, toolbar, contextual menus, window lifecycle, keyboard shortcuts, and Finder actions.
- Keep data-heavy asset and project views compact, readable, resizable, and usable at different display scales.
- Preserve Command-click and Shift-click multi-selection when opening a contextual menu.
- Avoid resetting selection merely because the user right-clicked within the existing selection.
- Use compiled bindings where practical and keep view code focused on presentation and interaction.
- Do not perform blocking filesystem, database, hashing, or network work on the UI thread.
- Marshal observable UI state changes onto the Avalonia UI thread.
- Long-running operations need visible progress when active, cancellation where safe, and actionable error messages.
- Preserve keyboard navigation, focus order, accessible names, and reasonable contrast.
- Keep macOS-only APIs behind platform services so shared code and tests remain portable.

## Concurrency and Reliability

- Pass `CancellationToken` through asynchronous call chains instead of discarding it at a layer boundary.
- Bound parallel filesystem and network work to avoid saturating storage, memory, or bandwidth.
- Serialize or coordinate writes that affect the same database, project, or remote manifest.
- Make retries safe. A retry must not duplicate an asset, publication, backup record, or destructive operation.
- Persist state transitions only when their external side effects are known, and represent partial states explicitly.
- Surface background exceptions through the runtime log and task UI; do not silently swallow them.

## Build and Test Commands

Run commands from the repository root on a Mac with the SDK selected by `global.json`:

```bash
make restore
make build
make test
```

Equivalent direct commands are documented in `README.md`. Targeted tests can be run with the relevant project under `tests/`.

- Use Release configuration for final verification.
- A non-macOS build or unit-test run may provide useful feedback, but it does not validate macOS lifecycle, Keychain, Finder, volume, application bundle, architecture, signing, or icon behavior.
- Do not claim macOS verification unless the change was exercised on a supported Mac.
- Never run integration tests with real user credentials, real cloud buckets, real publishing sites, or real asset directories.

Add or update tests in proportion to risk. Changes to destructive operations, migrations, identity, secrets, project conflict handling, and restore logic require focused regression tests before implementation is considered complete.

## Packaging, Icons, and Release Verification

- `make app` packages the Apple Silicon target through `scripts/build-app.sh`; `BEACON_ARCH` accepts only `arm64`.
- The script produces a self-contained `.app` under `build/osx-arm64`.
- The current ad-hoc signature is for local development only. Public distribution requires a Developer ID identity, hardened runtime, appropriate entitlements, notarization, and staple verification.
- `CDSI.Agent.Mac/Assets/logo.png` is the icon source. Generated sizes, `Assets.car`, and `Beacon.icns` must remain synchronized.
- Regenerate icon assets only on a Mac with the required Apple tools by using `scripts/generate-icons.sh`.
- Run `scripts/generate-icons.sh --check` after icon or generator changes.
- Do not edit generated icon artifacts independently of their source and integrity manifest.
- Validate the final app with `plutil`, architecture checks, signature verification, and a launch smoke test on a supported Apple Silicon Mac.

## Versioning

- Read the version from the root `VERSION` file. Do not hard-code a separate product version in source, project files, the About UI, package names, or update metadata.
- The format is `x.y.zz`, with the final component ranging from `10` through `99`.
- After `x.y.99`, advance to `x.(y+1).10`.
- Code or release commits increment the version exactly once.
- Documentation-only changes do not require a version bump unless they intentionally describe a new release.
- Release tags use `v<version>` and must match `VERSION`.
- Do not publish a release until the supported arm64 target has been built, tested, and exercised on a supported Apple Silicon Mac.

## Git and Change Discipline

- Preserve existing user changes and unrelated work.
- Keep edits scoped to the requested behavior and follow nearby code patterns.
- Do not use destructive Git commands to discard work.
- Do not commit, tag, push, publish, or rewrite history unless the user asks.
- Inspect the complete diff before committing.
- Do not commit secrets, local databases, logs, test results, build output, user-specific IDE state, signing material, or notarization credentials.
- Keep third-party notices accurate when dependencies are added, removed, or updated.
- Do not modify upstream license texts under `Legal/` except when deliberately updating the corresponding dependency material.

## Definition of Done

A change is complete only when:

- The requested behavior is implemented without overstating adjacent capabilities.
- Relevant tests pass, and macOS-only verification gaps are stated clearly.
- Error, empty, cancellation, retry, and partial-success states are handled where applicable.
- User data, source files, and credentials remain protected.
- Documentation and legal notices reflect user-visible or dependency changes.
- `VERSION` is updated only when the change requires a version increment.
- Release-affecting changes have been packaged and smoke-tested on supported Mac hardware.
