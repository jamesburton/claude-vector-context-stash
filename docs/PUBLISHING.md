# Publishing CCStash

CCStash ships as a single **portable, framework-dependent .NET tool** on NuGet.org, run via
`dnx CCStash`. This document covers the packaging strategy, the release process, and how to test
package consumption through `dnx`.

## Packaging strategy: portable, with selective native runtimes

CCStash is invoked with `dnx` (`dotnet tool exec`), which itself requires the .NET SDK to be
installed — so the .NET runtime is **always present** on any machine that can run the tool.
A self-contained or RID-specific package would therefore re-bundle a runtime the user already has,
for no benefit.

What *did* bloat the package was native libraries for mobile/web platforms a desktop/server CLI
never runs on. `Microsoft.ML.OnnxRuntime` and `SQLitePCLRaw` pull native binaries for every platform
they support; `PackAsTool` then bundles all of them:

| Removed (mobile / web) | Size |
| --- | --- |
| `ios/onnxruntime.xcframework.zip` | ~50 MB |
| `android/onnxruntime.aar` | ~42 MB |
| iOS / iOS-simulator `e_sqlite3.a` | ~14 MB |
| maccatalyst, browser-wasm | ~2 MB |

The `TrimUnusedNativeRuntimes` target in `src/CCStash/CCStash.csproj` drops native assets at
publish/pack time for RIDs under `runtimes/{ios*,android*,maccatalyst*,browser-*}`. This is a
**denylist** of platforms the CLI never targets, chosen deliberately over a desktop allowlist so that
every desktop/server RID stays intact — including managed RID assets such as `runtimes/win/lib`
(e.g. `System.Diagnostics.EventLog.dll`) that .NET's default Hosting loads on Windows. An allowlist
would strip those and break the tool on Windows.

This takes the package from ~164 MB to ~55 MB while keeping it a single cross-platform artifact.
Normal `dotnet build` / `dotnet test` are unaffected (the filter runs only on publish/pack; tests
load natives from the NuGet cache).

> **Future option — RID-specific packaging.** If CCStash ever needs to run where no .NET SDK is
> installed, switch to self-contained per-platform packages via `ToolPackageRuntimeIdentifiers`
> (see [Create RID-specific tools](https://learn.microsoft.com/dotnet/core/tools/rid-specific-tools)).
> Each user then downloads only their platform's package, at the cost of bundling the runtime and a
> more involved multi-package publish (the pointer package must be pushed last). Not worth it while
> distribution is `dnx`-only.

## Versioning

The package version is `<Version>` in `src/CCStash/CCStash.csproj`. Bump it for every release; the
release tag must match (`v<Version>`). The release workflow overrides the packed version from the
tag, so the tag is the source of truth at release time.

## Release process

Releases run from [`.github/workflows/release.yml`](../.github/workflows/release.yml).

1. Ensure `main` is green — CI (`ci.yml`) runs the full test suite (with the ONNX model and a Qdrant
   service) on every push and PR to `main`. Tags are cut from `main`.
2. Bump `<Version>` in `src/CCStash/CCStash.csproj`, commit, and merge to `main`.
3. Tag and push:
   ```bash
   git tag v0.1.6
   git push origin v0.1.6
   ```
4. The workflow's `pack` job restores, builds, packs (version from the tag), and **smoke-tests the
   packaged tool against the local artifacts feed**. The gated `publish` job then exchanges a GitHub
   OIDC token for a short-lived NuGet key and pushes with `--skip-duplicate`.

**Dry run:** trigger the workflow manually (Actions → Release → Run workflow) with `push` unchecked
to pack and smoke-test without publishing.

### Trusted Publishing (OIDC — no long-lived API key)

Publishing uses [NuGet Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing):
the `publish` job requests a GitHub OIDC token (`permissions: id-token: write`), and `NuGet/login@v1`
exchanges it for a temporary (~1 hour) API key. No secret key is stored.

**One-time setup:**

1. On nuget.org → your username → **Trusted Publishing**, add a policy:
   - **Repository Owner:** your GitHub owner
   - **Repository:** `claude-vector-context-stash`
   - **Workflow File:** `release.yml` (filename only, no path)
   - **Environment:** `nuget` (must match `environment: nuget` on the `publish` job)
2. Add a repository **secret** `NUGET_USER` = your nuget.org **profile name** (not your email).
3. (Recommended) Add a GitHub Actions **environment** named `nuget` with any protection rules you
   want (e.g. required reviewers) — it gates the publish step.

> The `publish` job is split out and bound to the `nuget` environment so environment protection
> wraps only the push; the `pack`/smoke-test job runs unconditionally. For a private repo the policy
> is provisionally active for 7 days until the first successful publish locks it to the repo/owner IDs.

## Testing consumption via `dnx`

The smoke test deliberately uses the **local** `./artifacts` feed, not NuGet.org. After a real push,
NuGet.org indexing lags by minutes, so an immediate `dnx CCStash` from nuget.org can fail
spuriously — prefer the local feed for validation.

**Against the local build (this repo):** `dnx-nuget.config` adds `./artifacts` as a source, so once
you `dotnet pack ... -o artifacts`:

```bash
dotnet dnx --configfile dnx-nuget.config CCStash -- status
dotnet dnx --configfile dnx-nuget.config CCStash -- gc --dry-run
```

**Against an arbitrary local feed:**

```bash
dotnet tool install --tool-path ./_smoke --add-source ./artifacts CCStash
./_smoke/ccstash status
```

**From NuGet.org (after indexing):**

```bash
dnx CCStash            # latest
dnx CCStash@0.1.6      # pinned
```

## Local artifacts hygiene

`artifacts/` is git-ignored. Old `*.nupkg` files accumulate there from repeated local packs — keep
only the latest (or clear the folder; the release workflow always packs fresh in CI). This is a
local-disk concern only; nothing is published from `artifacts/` except by you, manually, or by the
release workflow's freshly packed output.
