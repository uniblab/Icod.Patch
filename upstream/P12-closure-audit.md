# Icod.Patch Phase P12 closure audit

## Closure result

Phase P11B and the implementation portion of Phase P12 are complete. Production mutation has one path through `PatchE6Transaction`; the provisional P9 transaction and Patch-local capability vocabulary are absent.

The original CoreUtils closure pass covered the complete release option inventory, parser corpora, four-format Diffutils interoperability, GNU 2.8 opt-in differential checks, containment, resource limits, cancellation, POSIX defaults, public surface, formatting, and dependency classification.

The standalone G8 repository now contains the production source, dedicated test project, complete fixture corpus, build/test scripts, and three-platform CI configuration. The test suite preserves the original byte-sensitive fixture corpus and exercises the security, canonical-path, metadata, transaction, matching, artifact, compatibility, and process-host boundaries required for extraction closure.

## Functionality still not implemented

The following are real residual differences and must not be represented as complete GNU patch 2.8 parity:

1. **Conditional output (`-D` / `--ifdef`).** The managed engine does not emit preprocessor if/then/else output.
2. **Read-only policy (`--read-only=ignore|warn|fail`).** Access failures are controlled operational trouble, but the selectable GNU policy is not modeled.
3. **GNU debugging flags (`-x` / `--debug`).** The normal release is not built with a GNU `DEBUGGING` compatibility surface.
4. **Obsolete backup invocation.** The historical `-b SUFFIX ORIGFILE PATCHFILE` compatibility form is not accepted.
5. **Git extension payloads.** Git binary patch payloads and Git copy/rename metadata are not parsed as patch operations. Ordinary textual unified sections surrounded by Git/mail metadata remain supported when detectable.
6. **Hard-link topology.** A patched pathname is replaced as its own filesystem entry; replacement does not preserve or update every peer in an existing hard-link set.
7. **FIFO and other special-file targets.** Patch applies to regular file content and controlled link cases; it does not stream mutations into FIFOs or recreate device, socket, or provider-owned special objects.
8. **Crash journaling.** The transaction layer supplies staged replacement, rollback, cleanup, and explicit atomicity/durability reporting, but not a persistent journal that can recover a transaction after process or machine loss.
9. **Byte-for-byte diagnostic and interactive transcript parity.** Status classes and policy are covered, but locale-specific wording, every prompt transcript, and all GNU upstream tests are not claimed identical.

These items are deliberate final limitations unless a later roadmap explicitly reopens Patch.

## Platform limitations

- Atomic replace, atomic publish, atomic delete, and directory durability are reported by the active transactional-replacement provider. Preferred-atomic policy may use a diagnosed non-atomic fallback where the host cannot supply an atomic primitive.
- Unix mode, owner, group, and timestamp fidelity depends on the neutral metadata/mutation providers and process privileges.
- Windows symbolic-link creation/following behavior depends on host permissions and developer-mode policy. Junctions and other reparse objects are distinguished and are not treated as ordinary symbolic links.
- The containment policy is intentionally stricter than historical GNU patch: selected targets and generated artifacts may not escape the physically canonical `-d` root through `..`, volume changes, links, junctions, or reparse resolution.

## Extraction state

The production project is now staged in the dedicated `Icod.Patch` repository with these final dependencies:

- `Icod.CommandFramework` 1.1.0;
- `Icod.Path` 1.0.0.

There is no production dependency on `Icod.CoreUtils.Shared`, `Icod.DiffUtils.Shared`, native `patch`, or native `ed`. Patch-specific parsing, matching, filename evidence, artifact naming, rejects, partial-application policy, and GNU diagnostics remain in `Icod.Patch`.

The dedicated `Icod.Patch.Tests` project and complete fixture corpus are migrated into this repository. The test project references the repository-local product project only; Diffutils interoperability remains a captured textual-format boundary rather than a runtime dependency.

The repository build scripts and CI configuration execute clean/restore/build/test across the standalone solution. Pull requests validate `Staging` on Windows, Ubuntu, and macOS; pushes to `main` validate `Release` on the same matrix.

Physical removal of the migrated Patch source/test tree from `Icod.CoreUtils` remains a separate CoreUtils cleanup operation after independent standalone validation.
