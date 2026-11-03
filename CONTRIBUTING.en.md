# Contributing to MyProxy

MyProxy is aimed at developers who deploy their own server. The clients reduce everyday use to pairing and a connection switch; the server, certificates, routing and release configuration are the deployer's responsibility. Changes should put lightness, stability and reproducibility first.

## Getting started

Start by reading the [README](README.en.md), the [architecture](docs/architecture.en.md), the [deployment notes](docs/deployment.en.md) and the [API contract](server/docs/openapi.yaml). The main development branch is `main`. Create a branch from your own fork and submit changes with a clear scope.

Source checks and compilation can use the `.invalid` example entry point that ships with the repository; before connecting for real, replace deployment.json and deploy your own HTTPS API and 3x-ui. Keep development or test credentials local, and redact logs before submitting them.

Run the checks relevant to your change:

~~~sh
python scripts/release_verify.py check
python -m unittest discover -s scripts -p 'test_*.py' -v
python -m unittest discover -s server/tests -v
dotnet test windows/MyProxy/Tests/MyProxy.Tests.csproj -c Debug
dotnet test linux/MyProxyLinux/tests/MyProxy.Linux.Tests.csproj -c Release
~~~

Windows tests run on Windows, and Linux system integration tests run on Linux. Tests involving the real system proxy or auto-start may change local settings, so use an isolated test environment. The Windows/Linux SDK version is pinned by global.json; Android's JDK, SDK and Gradle versions are in the README and the project configuration.

Run in `android/MyProxyAndroid/`:

~~~sh
./gradlew --no-daemon lintRelease testDebugUnitTest assembleRelease lintStaging testStagingUnitTest assembleStaging
~~~

On Windows, use gradlew.bat. When changing the Android native library, also read the [rebuild and replacement guide](docs/android-core-rebuild.en.md). Documentation-only changes need only their content and links checked; behaviour changes should come with regression results that demonstrate the problem and the fix.

## Submitting and reviewing

A PR describes the triggering conditions, the behaviour after the change and the verification results. When interfaces, configuration or compatibility change, update the public documentation and contract at the same time. Avoid bundling formatting, dependency upgrades and unrelated refactoring.

Connecting, disconnecting, cancelling, restoring and process exit must all have a well-defined outcome. Keep standard TLS verification, the API's refusal to follow redirects, the proxy restore backup and update signature verification. Error messages tell users what to do next; network configuration details belong in the deployer documentation and the necessary explanations in settings.

Before adding a dependency, explain its purpose, size and maintenance cost, and update the version and license materials. When changing bundled native assets, update their digests, corresponding source and rebuild path at the same time. Never commit installers, private keys, certificates, databases, personal directories, live credentials or build caches.

You may use AI to help with development. Contributors still need to understand their changes, check sources and licenses, and provide verification results. Reviews focus on code, facts and reproduction; respect contributors of every level of experience.

## Feedback

Use the repository's issue templates for ordinary bugs and feature suggestions. Report security vulnerabilities privately as described in [SECURITY.md](SECURITY.en.md). Support boundaries, the release process and repository settings are in the [maintenance notes](docs/maintaining.en.md).

By submitting a contribution, you confirm that you have the right to provide it and agree that the project's own parts are distributed under the [MIT](LICENSE) license; third-party code must keep its original license. The project does not require copyright assignment, nor the use of any particular development tool.
