# Maintenance and releases

This repository provides the framework, source code and build inputs for developers who run their own server. Ordinary users' connection entry point is configured by the deployer; the public repository provides no shared accounts and no ready-made VPN service.

## Setting up the public repository for the first time

The public repository is [KunyQi/MyProxy](https://github.com/KunyQi/MyProxy), with `main` as the default branch. Push only the cleaned-up public branch; other branches, tags and old history of the internal repository stay local, and are not uploaded with `--all` or `--mirror`.

Before going public for the first time, enable Issues, Private vulnerability reporting, Dependabot alerts and the available secret scanning/push protection in the GitHub repository settings. Enable GitHub Actions and run the cross-platform CI and CodeQL workflows; the security reporting entry should be reachable from the Security page.

Configure a ruleset for the default branch: merge through PRs, require at least one review, resolve review conversations, and require these checks to succeed: Release policy and packaging tests, Server tests and deployment syntax, Android lint, unit tests, and release APK, Linux client build, tests, and tray GUI, Windows tests, core hashes, and CI artifact. Pick the names from the actual check list once the first workflows have completed. Include CodeQL's C#, Java/Kotlin, Python and Actions analyses in review.

Review Dependabot PRs and code scanning results regularly: reproduce and test first, then merge. Automated update tools do not replace checking licenses, digests, ABI and runtime behaviour. If the repository's default branch is renamed, update the workflows' trigger branches and this document accordingly.

## Verification before a release

First run the checks from the [contribution guide](../CONTRIBUTING.en.md) that are relevant to this version. The license check verifies the project's MIT license, the digests of third-party license texts, the actual native modules and the license index, and requires the native wrapper source and rebuild documentation to be present.

~~~sh
python scripts/release_verify.py check
python -m unittest discover -s scripts -p 'test_*.py' -v
python -m unittest discover -s server/tests -v
~~~

Before distributing production clients, change deployment.json to your own HTTPS origin and verify the configuration and deployment:

~~~sh
python scripts/release_verify.py check --require-configured
~~~

In an actual deployment, confirm pairing, connecting, cancelling, disconnecting, system proxy restoration, device revocation and server certificate renewal; use the target phones for Android and the target desktop environments for Linux. Standard TLS failures and a lost daemon must show up as clear error states, never as a leftover "connected" display.

When replacing Xray/libv2ray, update the corresponding version, binary digests, Go build info, license index, source download directions and rebuild instructions. Keep the source and trim digests of the rule data. Make sure the license materials of new dependencies are not lost during compression, trimming or packaging.

## Installers and source code

The private admin console provides a wizard for signed manifests, installer uploads and release assignment; the procedure and the first-time public key configuration are in [Graphical release publishing and assignment](release-publishing.en.md).

The Windows packaging entry point is `python scripts/package_delivery.py`; use `--preflight` for pre-flight checks. Linux uses `python scripts/package_linux.py --preflight` and `--build --rid linux-x64`. Both carry the public documentation, the MIT license and the third-party license directory. Windows artifacts need the .NET 8 Desktop Runtime; in the Linux package, the CLI and GUI share a self-contained runtime.

Android Release builds are unsigned by default; sign them with your own key, and verify that assets/myproxy-licenses/ in the package matches the actual source version. Native library changes and installing your own signed build are covered in the [Android guide](android-core-rebuild.en.md). For every platform, state clearly the signing status, supported architectures, version, digests and the verified scope.

Near the installer download entry, provide directions for downloading the corresponding MyProxy source and third-party source, and keep them freely available; never offer binaries alone. Provide checksums with each release and update the CHANGELOG. Signing private keys, real server configuration and the runtime database stay in the deployer's private environment.

Checksums for the release manifest can be generated with the existing tools. The SBOM command must specify the release directory and version; for example, after publishing the Linux package to dist/MyProxy-linux-x64, run:

~~~sh
python scripts/release_verify.py sbom --root dist/MyProxy-linux-x64 --version 0.1.0 --output dist/MyProxy-linux-x64.sbom.json
~~~

Replace the path and version with the actual artifacts, and keep the SBOM and release checksums. The trust keys for signed installers, update metadata and distribution addresses are configured by the deployer. Automatic update checks are off by default; verify the actual trust chain before going live.

## Versions and support

Release notes should cover at least new behaviour, fixes, compatibility changes, verification results and the platform scope not covered. Changes that break the API, deployment.json or the local state format should provide a migration path. If an already distributed binary needs to be withdrawn, keep the fix announcement and the way to obtain the corresponding source.

0.1.x is currently a public beta. Publishing the source and passing the tests shows that an engineering foundation exists; ongoing vulnerability handling, dependency updates, platform acceptance and reproducible releases need to be kept up in later versions.
