# C#/.NET build and packaging workflow

This directory is the shared implementation behind the repository's local build scripts and GitHub Actions workflows.

The design follows the canonical `uniblab/.github` pattern. It assumes one root solution, .NET 10, optional NuGet publication, and optional executable RID archives. Repository and package identities are discovered from the solution/MSBuild and generated packages rather than hard-coded from the GitHub repository name.

## Validation ladder

| Lifecycle | Configuration | Work |
| --- | --- | --- |
| local `build.cmd` / `build.sh` | `Debug` | clean, restore, build, test, pack, exact package validation |
| pull request | `Staging` | Windows/Linux/macOS build and test; Linux also validates generated NuGet artifacts |
| default branch | `Release` | six-runner Windows/Linux/macOS x64/ARM64 distribution validation |
| `v<semver>` tag | `Release` | package/archive production and publication |

`Get-RepositoryMetadata.ps1` exports a repository-relative solution path so metadata produced on Linux can be consumed safely by Windows and macOS jobs.

`BuildReleaseArchive.ps1` discovers executable projects from `OutputType`; for this repository it discovers the `patch` executable automatically and produces framework-dependent single-file archives for `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, and `osx-arm64`.

Tagged releases require the tagged commit to be contained in the default branch. Packages are selected for publication only when their actual nuspec version matches the `v<semver>` tag. NuGet.org publication uses the GitHub `Release` environment and OIDC Trusted Publishing; GitHub Packages uses `GITHUB_TOKEN`. Both publication paths use `--skip-duplicate`.
