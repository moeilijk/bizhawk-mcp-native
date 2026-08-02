# CI & releases

Workflow: `.github/workflows/build-and-release.yml` — builds the external tool against **fresh BizHawk builds** on every run, so artifacts always match current BizHawk versions.

## Triggers

| Trigger | Behavior |
|---|---|
| `workflow_dispatch` (manual, Actions tab) | Builds both flavors, uploads artifacts |
| `push` of tag `v*` | Same builds + attaches zips to a GitHub release |

## Flavors (matrix)

| Flavor | BizHawk source | Resolved via |
|---|---|---|
| `stable` | Latest official release tag | GitHub API `releases/latest` → `win-x64.zip` asset |
| `dev` | Latest dev build | `nightly.link/TASEmulators/BizHawk/workflows/ci/master/BizHawk-dev-windows.zip` |

Each job: download → extract (`dll/` + `EmuHawk.exe` sanity-checked) → `dotnet build -c Release -p:BizHawkInstallDir="$(pwd)/bizhawk"` → stage `BizHawkMcp.dll` + NuGet deps → zip as `BizHawkMcp-<flavor>-<bizhawk-tag>.zip`.

## Packaging rules

- Only the tool + its NuGet dependencies ship (`bin/Release/*.dll`). BizHawk assemblies are `Private=false` and **never** included — users already have them.
- The zip must be extracted into `<install>/ExternalTools/`.
- If the packaging step in the workflow and the csproj's `CopyToExternalTools` drift apart, keep the **workflow** authoritative for CI artifacts.

## Release job

On tag push: `gh release create <tag> artifacts/**/*.zip --generate-notes` (idempotent — `--clobber` on re-upload). Release naming convention: `v0.1.0`, `v0.2.0`, … (own project version, independent of BizHawk's).

## Notes

- Everything runs on `ubuntu-latest` — `net48` + `Microsoft.NETFramework.ReferenceAssemblies` make Windows runners unnecessary.
- Manual smoke test after publishing: download the zip, extract into `ExternalTools/`, load the tool, run the curl sequence from `docs/MCP-PROTOCOL.md`.
