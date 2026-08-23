# Icod.Patch Development and Extraction Roadmap

## Status

This is the standalone roadmap for `Icod.Patch`. It supersedes the co-resident Patch roadmap maintained in `Icod.CoreUtils` before Completion Gate G8 extraction.

The standalone repository was seeded from the CoreUtils G7 merge snapshot at commit `945a33c7ec80222983a37a35084a93060bf7c519`. Production code was migrated without behavioral rewriting; repository/project wiring and one process-host test repository sentinel were adjusted for the new repository boundary.

The authoritative behavioral baseline is GNU patch 2.8.

## Current contract

| Item | State |
|---|---|
| Repository | `Icod.Patch` |
| Product project | `Icod.Patch.csproj` |
| Test project | `tests/Patch.Tests/Icod.Patch.Tests.csproj` |
| Solution | `Icod.Patch.sln` |
| Executable assembly | `patch` |
| Public command facade | `Icod.Patch.Command` |
| Target framework | `net10.0` |
| Language | C# 13 |
| Version | `1.0.0` |
| Upstream baseline | GNU patch 2.8 |
| Neutral dependencies | `Icod.CommandFramework` 1.1.0; `Icod.Path` 1.0.0 |
| Runtime Diffutils dependency | None |
| Required CI runners | `windows-latest`, `ubuntu-latest`, `macos-latest` |

## Completed implementation roadmap

The original P0–P12 work is closed. The implementation now includes:

- **P0–P4 / Wave A — source and syntax:** byte-preserving bounded patch input, structural scanning, source mapping, unified/context/normal/ed parsing, immutable syntax models, malformed-input hardening, and resource limits.
- **P5–P6 / Wave B1 — application engine:** indexed virtual targets, exact/offset matching, fuzz, whitespace policy, reversal, prerequisites, merge/diff3 output, deterministic candidate limits, cancellation, and immutable result ownership.
- **P7 / Wave B2 — path planning:** explicit operands, `-d`, platform-aware `-p`, quoted and `Index:` names, GNU/POSIX candidate ordering, `Icod.Path` canonicalization, roots, volumes, links/reparse points, containment, virtual create/delete state, and multi-file aggregation.
- **P8 / Wave C — artifact policy:** targets, backups, rejects, alternate output, standard output, dry-run, prompting, quoting, timestamps, modes, metadata policy, artifact naming, and GNU-visible status behavior.
- **P9–P10 / Wave D — transaction contract:** per-file recovery units, secure sibling staging, flush-before-commit, identity revalidation, no-follow defaults, cancellation recovery, deterministic cleanup, rollback outcomes, metadata restoration, and explicit atomicity/durability capabilities.
- **P11A–P11B — shared E6 integration:** Patch artifacts adapt to the neutral transactional-replacement provider; the provisional command-local replacement implementation was removed.
- **P12 — final conformance:** final option inventory, source-defined unsupported capability diagnostics, GNU 2.8 provenance, parser/application corpus closure, Diffutils textual interoperability, public-surface closure, and documented residual limitations.

## Completion Gate G8 extraction

Repository-content work for G8 is complete:

- [x] establish the standalone `Icod.Patch` repository and solution;
- [x] migrate all Patch production source and Patch-owned upstream/conformance documentation;
- [x] replace the transitional CoreUtils Shared boundary with published `Icod.CommandFramework` 1.1.0 and `Icod.Path` 1.0.0;
- [x] migrate `Icod.Patch.Tests` and the complete fixture corpus;
- [x] preserve byte-sensitive fixtures exactly;
- [x] keep Diffutils interoperability textual and remove project/runtime dependency on Diffutils;
- [x] add repository-local `build.sh` and `build.cmd` clean/restore/build/test verbs;
- [x] configure pull-request Staging clean/restore/build/test across Windows, Ubuntu, and macOS;
- [x] configure `main` Release clean/restore/build/test across Windows, Ubuntu, and macOS;
- [x] migrate Patch-owned roadmap/status documentation into the standalone repository.

The final execution gate is independent validation of the configured matrix. G8 should be marked fully closed in the CoreUtils migration roadmap only after the standalone CI is green and any platform-specific failures found by that run are resolved.

Removal of the migrated Patch production/test tree from `Icod.CoreUtils` is intentionally a later CoreUtils-side cleanup patch; this repository migration does not delete neighboring source.

## Test closure

The dedicated test suite covers the extraction-sensitive areas required by G8:

- parser and byte-preservation corpora for unified, context, normal, and ed input;
- LF, CRLF, CR, invalid-byte, NUL-bearing, and incomplete-record inputs;
- canonical path, containment, root/volume, symbolic-link, and reparse-point behavior;
- exact, offset, fuzz, whitespace, reversal, prerequisite, merge, and cancellation behavior;
- backup, reject, output, dry-run, quoting, timestamp, mode, and metadata behavior;
- transaction staging, revalidation, rollback, partial commit, cleanup, and injected failure matrices;
- process-host invocation and public command behavior;
- deterministic randomized/fuzz invariants; and
- opt-in Linux comparisons with an installed executable that identifies itself as GNU patch 2.8.

Ordinary test execution remains fully managed and does not require native `patch`, native `ed`, or a Diffutils runtime dependency.

## Dependency boundary

```text
Icod.CommandFramework 1.1.0     Icod.Path 1.0.0
              \                    /
               \                  /
                    Icod.Patch
                        |
                Icod.Patch.Tests
```

`Icod.Patch.Tests` references the repository-local product project. `Icod.Patch` owns Patch-specific parsing, matching, filename evidence, artifact naming, reject/backup/output policy, GNU diagnostics, and Patch-to-transaction translation.

## Deliberate 1.0 limitations

The following remain documented divergences from complete GNU patch 2.8 parity:

1. `-D` / `--ifdef` conditional output is not implemented.
2. `--read-only=ignore|warn|fail` selectable policy is not implemented.
3. GNU `DEBUGGING`-only `-x` / `--debug` is unavailable in the normal release.
4. The obsolete three-operand `-b SUFFIX ORIGFILE PATCHFILE` form is not accepted.
5. Git binary payloads and Git copy/rename metadata are not patch operations.
6. Replacement does not preserve hard-link-set topology across all peers.
7. FIFO, device, socket, and provider-owned special-file targets are not supported as mutation targets.
8. Transaction recovery is in-process; there is no persistent crash journal.
9. Locale-specific diagnostic wording and every GNU interactive transcript are not claimed byte-for-byte identical.

These limitations are release facts, not unfinished G8 extraction tasks. Any future decision to implement them belongs to this standalone repository.

## Post-G8 ownership

After CoreUtils removes its migrated copy, all future Patch implementation, compatibility, security, test, and release work belongs exclusively in `Icod.Patch`.

The authoritative upstream evidence remains under `upstream/`; fixture provenance remains under `tests/Patch.Tests/fixtures/`; source/test layouts are documented in their local `README.md` files.
