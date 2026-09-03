# Icod.Patch package and extraction metadata

## Identity

- Project/package: `Icod.Patch`
- Command/assembly: `patch`
- Version: `1.0.1`
- Target framework: `net10.0`
- Language policy: C# 13
- Behavioral baseline: GNU patch 2.8
- Public API facade: `Icod.Patch.Command`
- Exit classes: GNU-compatible `0`, `1`, and `2`, plus the command-framework canceled status for cooperative cancellation

Repository versioning is centralized in the root `Directory.Build.props`. `VersionPrefix` is the authoritative release-version literal, with `Version`, `PackageVersion`, `AssemblyVersion`, and `FileVersion` derived from it.

The NuGet package identity is explicitly `Icod.Patch`; it is intentionally independent of the command/assembly name `patch`. The package embeds the repository `README.md` as its NuGet readme and `icon.png` as its package icon, and declares `GPL-3.0-or-later` licensing metadata.

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
- Require a `v<semver>` release tag to match the actual `Icod.Patch` nuspec version before package publication.

## Test and CI closure

The standalone repository contains `tests/Patch.Tests/Icod.Patch.Tests.csproj` and the complete Patch fixture corpus migrated from the CoreUtils G7 snapshot.

The test project references only `Icod.Patch.csproj`; it does not reference `Icod.CoreUtils.Shared` or any Diffutils project. Fixture inputs retain their original bytes, including CRLF, NUL-bearing, invalid-byte, incomplete-record, and intentionally malformed cases.

Build and release automation follows the canonical `uniblab/.github` C#/.NET repository pattern:

- local `build.cmd` / `build.sh` use `Debug` and run `clean → restore → build → test → pack → validate` by default;
- pull requests use `Staging` on Windows, Linux, and macOS, with Linux also packing and validating exact NuGet artifacts;
- pushes to `main` use `Release` distribution validation across Windows/Linux/macOS on x64 and ARM64; and
- `v<semver>` tags contained in the default branch use `Release` to select matching packages, publish to configured registries, and build framework-dependent single-file archives for `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, and `osx-arm64`.

Repository metadata and executable discovery are MSBuild-driven rather than derived from the GitHub repository name. Tagged release publication requires the package's actual nuspec version to match the tag version.
