# CI & releases

Workflow: `.github/workflows/build-and-release.yml` — builds the external tool against **fresh BizHawk builds** on every run, so artifacts always match current BizHawk versions.

## Triggers

| Trigger | Behavior |
|---|---|
| `workflow_dispatch` (manual, Actions tab) | Builds both flavors, uploads artifacts |
| `push` of tag `v*` | Same builds + attaches zips to a GitHub release |

## Build matrix (flavor × platform)

| Flavor | Platform | BizHawk source | Resolved via |
|---|---|---|---|
| `stable` | `win` | Latest official release | GitHub API `releases/latest` → `*-win-x64.zip` asset |
| `stable` | `linux` | Latest official release | GitHub API `releases/latest` → `*-linux-x64.tar.gz` asset |
| `dev` | `win` | Latest dev build | `nightly.link/.../BizHawk-dev-windows.zip` |
| `dev` | `linux` | Latest dev build | `nightly.link/.../BizHawk-dev-linux.zip` |

Both BizHawk OS builds are compiled against (the tool is `net48` and runs on Windows .NET and Linux Mono, so the fixture pair guards both).

Each job: resolve → download → extract (`dll/` + `EmuHawk.exe` sanity-checked; `.tar.gz` top-level dirs are flattened) → `dotnet build -c Release -p:BizHawkInstallDir="$(pwd)/bizhawk"` → stage `BizHawkMcp.dll` + NuGet deps → zip as `BizHawkMcp-<flavor>-<platform>-<label>.zip` where `<label>` is the **BizHawk release version** for stable and the **dev commit short sha** for dev.

## Resolved build info

The resolve step writes a `build-info.json` into every zip with the exact BizHawk source the tool was built against:

- `bizhawk.tag` — stable: release tag (e.g. `2.11.1`); dev: `dev`.
- `bizhawk.commit` — full commit sha. Stable: the tag ref (`git/ref/tags/<tag>`); dev: `head_sha` of the latest successful `ci.yml` run on `master` (same run nightly.link serves).
- `tool.version` — own project version (`v*` tag push) or the workflow's `github.sha` on manual runs.
- Plus `platform`, `flavor`, `built_at` and `workflow.run_id`/`run_number`.

## Packaging rules

- Only the tool + its NuGet dependencies ship (`bin/Release/*.dll`). BizHawk assemblies are `Private=false` and **never** included — users already have them.
- The zip is rooted at the DLLs (`build-info.json` beside them); extract its contents into `<install>/ExternalTools/`.
- If the packaging step in the workflow and the csproj's `CopyToExternalTools` drift apart, keep the **workflow** authoritative for CI artifacts.

## Release job

On tag push: `gh release create <tag> artifacts/**/*.zip --generate-notes` (idempotent — `--clobber` on re-upload). Release naming convention: `v0.1.0`, `v0.2.0`, … (own project version, independent of BizHawk's). All 4 matrix zips are attached.

## Notes

- Everything runs on `ubuntu-latest` — `net48` + `Microsoft.NETFramework.ReferenceAssemblies` make Windows runners unnecessary.
- Manual smoke test after publishing: download the zip, extract into `ExternalTools/`, load the tool, run the curl sequence from `docs/MCP-PROTOCOL.md`.
