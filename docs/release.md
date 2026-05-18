# x-tunnel Client Release Process

This repository releases a Windows GUI client that bundles a pinned `x-tunnel.exe`
sidecar. The release process is designed to be reproducible from GitHub Actions
and locally from PowerShell.

## Release Inputs

Release configuration lives in `eng/release.json`:

- `.NET SDK`: pinned by both `global.json` and `eng/release.json`.
- NuGet dependencies: resolved with committed `packages.lock.json` files for the
  app, core library, and tests.
- `WiX Toolset`: pinned in `.config/dotnet-tools.json`.
- `x-tunnel` core repo/ref: checked out as a sibling repository and recorded in
  `release-manifest.json`.
- RIDs: `win-x64` and `win-arm64`.

For local packaging, keep the core repository next to this repository:

```powershell
C:\Users\liang\GitHub\x-tunnel
C:\Users\liang\GitHub\x-tunnel-client
```

## Local Gates

Run the full local packaging gate:

```powershell
.\scripts\package.ps1 -Configuration Release
.\scripts\verify-release.ps1
```

The package script runs:

- `dotnet restore` and `dotnet test`.
- `go test ./...` in the sibling `x-tunnel` repository.
- Cross-compiled Windows core sidecars for every configured RID.
- Framework-dependent portable zips.
- Self-contained portable zips.
- Self-contained MSI installers with WiX.
- MSI validation unless `-SkipMsiValidation` is explicitly passed.

The verifier checks `release-manifest.json`, `SHA256SUMS`, asset sizes and
hashes, required portable zip entries, and the MSI file signature.

## Artifacts

Each release writes assets under `artifacts\dist\`:

- `x-tunnel-client_<version>_win-x64_framework-dependent.zip`
- `x-tunnel-client_<version>_win-x64_self-contained.zip`
- `x-tunnel-client_<version>_win-x64_setup.msi`
- `x-tunnel-client_<version>_win-arm64_framework-dependent.zip`
- `x-tunnel-client_<version>_win-arm64_self-contained.zip`
- `x-tunnel-client_<version>_win-arm64_setup.msi`
- `release-manifest.json`
- `SHA256SUMS`

Portable zips include:

- `XTunnelClient.App.exe`
- `core\x-tunnel.exe`
- `version.json` with client/core commits and executable hashes

## GitHub Actions

`CI` runs on push and pull requests:

- Checks out this repo and the pinned core repo.
- Installs the pinned .NET and Go versions.
- Runs the package script for `win-x64`, including MSI.
- Runs the release verifier.
- Uploads CI packages as short-lived workflow artifacts.

`Release` runs on version tags and manual dispatch:

- Accepts tags shaped like `v1.2.3` or `v1.2.3-rc.1`.
- Verifies the tag, .NET tests, Go tests, and a package smoke.
- Builds all configured packages.
- Runs the release verifier.
- Generates GitHub artifact attestations.
- Uploads all assets to the GitHub Release.

The workflow uses least-privilege permissions by default. Write permissions are
limited to the attestation job and the final GitHub Release publishing job.

## Publishing

1. Update `eng/release.json` if the bundled core ref changes.
2. Run the local gates.
3. Commit the release config change.
4. Create and push a tag:

```powershell
git tag v0.1.0
git push origin v0.1.0
```

For a manual rerun, use the `Release` workflow and provide an existing tag. Use
the `core_ref` override only for an intentional rebuild with a different pinned
core source; the generated manifest records the actual core commit.

## Signing

The package script can Authenticode-sign release payloads when a certificate is
available:

```powershell
$env:WINDOWS_SIGNING_CERT_PASSWORD = "<certificate password>"
.\scripts\package.ps1 -Configuration Release `
  -Sign `
  -SignCertificatePath C:\secure\windows-signing.pfx
```

When the GitHub secrets `WINDOWS_SIGNING_CERT_BASE64` and
`WINDOWS_SIGNING_CERT_PASSWORD` are configured, the release workflow signs:

- `XTunnelClient.App.exe`
- `core\x-tunnel.exe`
- each MSI

Unsigned builds are still supported for private/internal testing, but public
trust-sensitive distribution should use a hardware-backed or cloud KMS-backed
certificate flow. Do not store a raw PFX in the repository.

## Rollback

Do not overwrite tags. If a release is bad:

1. Mark the GitHub Release as prerelease or delete the release assets.
2. Create a new patch tag from a fixed commit.
3. Let the release workflow produce fresh manifests and checksums.
