# Icod.Patch repository overview

> This document preserves the maintainer-oriented repository overview that formerly served as the root README. The root [`README.md`](../README.md) is now organized as a `patch(1)`-style command reference.


`Icod.Patch` is the standalone C# implementation of GNU `patch`, targeting `net10.0` with C# 13.

The authoritative behavioral baseline is GNU patch 2.8. Pinned release metadata and verification values are recorded in [`upstream/GNU-patch-2.8.md`](../upstream/GNU-patch-2.8.md), the final option/conformance inventory is recorded in [`upstream/GNU-patch-2.8-option-matrix.md`](../upstream/GNU-patch-2.8-option-matrix.md), and deliberate residual differences are recorded in [`upstream/P12-closure-audit.md`](../upstream/P12-closure-audit.md).

## Extraction status

This repository contains the standalone G8 extraction of `Icod.Patch` from `Icod.CoreUtils`. The production source remains byte-for-byte aligned with the CoreUtils G7 merge snapshot, commit `945a33c7ec80222983a37a35084a93060bf7c519`, except for repository/project infrastructure required by the standalone repository.

The dedicated `Icod.Patch.Tests` project and its complete fixture corpus are now part of the standalone solution. The migrated tests cover parser/application behavior, canonical-path and containment policy, metadata and transaction recovery, fuzz and offset matching, reversal, backup/reject/output behavior, compatibility, process-host invocation, deterministic fuzzing, and opt-in GNU patch 2.8 differential checks.

The standalone projects reference only their repository-local product project and published neutral dependencies. They do not depend on `Icod.CoreUtils.Shared` or `Icod.DiffUtils.Shared`. Removal of the now-migrated Patch files from `Icod.CoreUtils` is intentionally a separate repository-cleanup step after this standalone repository is independently validated.

The standalone development and extraction record is maintained in [`Icod.Patch-Development-Roadmap.md`](../Icod.Patch-Development-Roadmap.md).

## Current implementation

Patch Waves A through D and Phases P11A–P12—Phases P0 through P12—are implemented. The command:

- uses the published `Icod.CommandFramework` command and filesystem contracts;
- preserves patch bytes, source offsets, line endings, incomplete records, and bounded spill storage;
- parses unified, context, normal, and patch-compatible ed scripts into immutable common models;
- applies exact, offset, fuzz, whitespace, reverse, prerequisite, and merge policy to immutable virtual targets;
- consumes `Icod.Path` for lexical and physical path resolution, roots, volumes, links, reparse points, missing components, and containment;
- implements explicit original-file operands, `-d`, component-aware `-p`, quoted names, `Index:` evidence, GNU/POSIX candidate ordering, `/dev/null` creation/deletion forms, multiple file patches, and per-file status aggregation;
- implements reject, backup, output, dry-run, prompt, quoting, status, mode, timestamp, and metadata policy above the shared filesystem contracts;
- stages complete exclusive sibling temporary files and flushes them before destination mutation;
- revalidates filesystem identity immediately before commit and preserves explicit no-follow policy;
- recovers target-related artifacts in per-file units while retaining completed earlier units for GNU-visible multi-file partial success; and
- distinguishes failed-before-commit, rolled-back, partially committed, rollback-incomplete, and cleanup-incomplete transaction outcomes.

The containment policy is intentionally stricter than historical GNU patch: every selected target, output, backup, and reject artifact must remain within the physically canonical `-d` working root. Parent traversal, cross-volume targets, and link/reparse resolutions that escape that root are rejected. Terminal links are rejected by default and followed only with `--follow-symlinks`.

## Dependency boundary

The standalone executable depends on:

- `Icod.CommandFramework` 1.1.0; and
- `Icod.Path` 1.0.0.

`Icod.Patch` consumes textual patch streams and does not reference `Icod.DiffUtils.Shared`, invoke native `patch`, invoke native `ed`, or depend on `Icod.CoreUtils.Shared`.

## Final limitations

Icod.Patch 1.0 deliberately does not implement `-D`/`--ifdef`, `--read-only=ignore|warn|fail`, the GNU `DEBUGGING`-only `-x` surface, the obsolete three-operand `-b` compatibility form, Git binary/copy/rename payloads, hard-link-set topology updates, or FIFO/device/socket targets. The transaction layer does not provide persistent crash-recovery journaling. See the closure audit for the complete platform and behavior ledger.

## Build

On Unix-like hosts:

```sh
./build.sh
```

On Windows:

```bat
build.cmd
```

With no argument, both scripts run `clean`, `restore`, `build`, and `test`. Individual verbs may also be run directly:

```text
clean
restore
build
test
```

The standalone CI performs clean/restore/build/test across `windows-latest`, `ubuntu-latest`, and `macos-latest`:

- pull requests use the `Staging` configuration; and
- pushes to `main` use the `Release` configuration.

The Linux GNU differential tests remain opt-in: they execute only when the host has an installed executable that identifies itself specifically as GNU patch 2.8. Ordinary tests have no native `patch` or `ed` dependency.

## Authorship and upstream attribution sources

The root man-page-style README uses upstream attribution researched from GNU-maintained sources:

- GNU Diffutils' overview states that GNU `patch` was written mainly by Larry Wall and Paul Eggert, with GNU enhancements from Wayne Davison and David MacKenzie: <https://www.gnu.org/software/diffutils/manual/html_node/Overview.html>.
- The GNU `patch(1)` manual credits Larry Wall with the original implementation, Paul Eggert with major GNU development, Wayne Davison with unified-diff support, David MacKenzie with configuration and backup support, and Andreas Grünbacher with merge support: <https://www.gnu.org/software/diffutils/manual/>.
- GNU's people page lists Andreas Grünbacher as a co-maintainer of GNU patch: <https://www.gnu.org/people/people.en.html>.
- The GNU patch 2.8 release announcement lists the contributors to that release and gives special thanks to Paul Eggert for the majority of its changes: <https://lists.gnu.org/archive/html/info-gnu/2025-03/msg00014.html>.

The managed-port credit uses the public author identity recorded by this repository's signed merge history: Timothy J. Bruce <uniblab@hotmail.com>.
