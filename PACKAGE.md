# Icod.Patch package and extraction metadata

## Identity

- Project: `Icod.Patch`
- Command/assembly: `patch`
- Version: `1.0.0`
- Target framework: `net10.0`
- Language policy: C# 13
- Behavioral baseline: GNU patch 2.8
- Public API facade: `Icod.Patch.Command`
- Exit classes: GNU-compatible `0`, `1`, and `2`, plus the command-framework canceled status for cooperative cancellation

## Direct dependencies

| Dependency | Ownership |
|---|---|
| `Icod.CommandFramework` 1.1.0 | Published neutral command, byte-I/O, metadata, mutation, and transactional-replacement infrastructure. |
| `Icod.Path` 1.0.0 | Published neutral lexical/physical path, identity, link, reparse, and containment contract. |

`Icod.Patch` has no production dependency on `Icod.CoreUtils.Shared`, `Icod.DiffUtils.Shared`, or `Icod.LineEditor`.

## Patch-owned behavior

The following remain owned by Patch:

- patch-source scanning and all four syntax parsers;
- immutable patch models and byte-preserving source maps;
- hunk matching, offset, fuzz, whitespace, reversal, prerequisite, and merge policy;
- filename evidence and GNU/POSIX candidate ranking;
- backup, reject, alternate-output, and partial-application policy;
- Patch-to-transaction artifact translation; and
- GNU option semantics, diagnostics, quoting, and exit-status aggregation.

## Packaging conditions

- Preserve UTF-8 with LF line endings.
- Preserve XML documentation generation and the Debug/Staging/Release configuration policy.
- Preserve the GNU patch 2.8 provenance and conformance documents under `upstream/`.
- Do not introduce a runtime dependency on Diffutils or LineEditor.
- Publish the documented limitations from `upstream/P12-closure-audit.md` with any package release.

## Deferred test tranche

The production-side G8 migration intentionally omits `Icod.Patch.Tests` and its fixture corpus. Those files remain authoritative in `Icod.CoreUtils` until they are migrated and validated in a separate step. The standalone solution and CI therefore perform build validation only at this stage.
