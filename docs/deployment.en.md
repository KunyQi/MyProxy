# Configuration, build and deployment

There is only one entry point, shared by builds and deployments. Keep environment parameters in configuration, and leave the usage steps to the clients.

## 1. Prepare dependencies

Install the .NET SDK 8.0.424 specified in global.json and Python 3.12+. Android builds need JDK 17, Android SDK platform 37.0 and Build Tools 37.0.0, with JAVA_HOME and ANDROID_HOME set on your machine. SDKs, caches and keys are not part of the source tree.

## 2. Set the single entry point

Change api_base_url in deployment.json at the repository root. The value must be an HTTPS origin, for example https://api.example.com:8443; if the port is omitted, 443 is used. Do not put credentials, an API path, a query or a fragment in the URL.

The clients read this file at build time, so rebuild them after changing it. Debug builds keep a local development entry point. The placeholder .invalid address is never accepted as a usable production configuration.

By default, the connectivity check uses `GET /connectivity-check` on the same server and verifies an empty HTTP 204 through the tunnel. If you need fallback check endpoints, add `connectivity_check_urls` to the same file:

~~~json
{
  "api_base_url": "https://api.example.com:8443",
  "connectivity_check_urls": [
    "https://api.example.com:8443/connectivity-check",
    "https://check.example.com/204"
  ]
}
~~~

The array accepts one to four HTTPS URLs; paths are allowed, credentials, queries and fragments are not. Every endpoint must use a valid certificate and return an empty 204; redirects are not followed. All three clients use the same candidate list and the same allowlist of probe hosts, try the next candidate on failure, and refuse to confirm the connection if every candidate fails. Without the array, no third-party probe service is needed. In Debug builds the control plane may use a local API, but the tunnel check still uses the HTTPS candidates configured here; an emulator address cannot be used as the check target of a remote tunnel. Deployment scripts, packaging and rollback keep the whole configuration file.

The server's data-plane host is taken from this URL by default, and MYPROXY_SERVER_HOST can specify a different one. The control-plane port and the Xray inbound port are independent of each other; two services cannot share one port on the same listening address. The API can use 8443 while Xray uses its own inbound port.

## 3. Deploy the server

Prepare a Linux host with systemd, nginx, a valid HTTPS certificate issued by a public CA together with its full chain, and a configured 3x-ui / Xray inbound. The certificate's domain name or IP must match the API entry point; arrange for certificate renewal and an nginx reload.

Deploy the shared configuration together with the server's runtime files. In production, MYPROXY_DEPLOYMENT_CONFIG points to the deployment configuration file. The API and the helper use the same configuration; the administrator token, device token secret, SSH private key and data-plane private key are provided through protected local configuration.

The generic deployment script requires the SSH host, identity and certificate files to be given explicitly, and runs pre-flight checks before making any remote change. The admin entry stays private. Configuration options, deployment commands and administrator access steps are in the [Server README](../server/README.en.md).

After installation, verify the API health status and the empty-204 check endpoint, then create users and pairing codes. Using your own certificate and data plane, check binding, connecting, stopping and revocation.

## 4. Build the clients

Windows:

~~~text
dotnet build windows/MyProxy/MyProxy.csproj -c Release
dotnet test windows/MyProxy/Tests/MyProxy.Tests.csproj -c Debug
~~~

The Windows network integration tests use a local HTTP mock service in Debug, which needs a `python` command that can be started. Release builds keep production HTTPS verification. Integration cases for the system proxy and the auto-start registry run in a dedicated test environment.

Android, run in android/MyProxyAndroid/:

~~~sh
./gradlew --no-daemon lintRelease testDebugUnitTest assembleRelease lintStaging testStagingUnitTest assembleStaging
~~~

On Windows you can use gradlew.bat. The Release APK is unsigned by default; sign it with your own long-term key before installing, and keep the same signing identity for upgrades installed over the top.

Linux:

~~~text
dotnet build linux/MyProxyLinux/cli/MyProxy.Linux.Cli.csproj -c Release
dotnet build linux/MyProxyLinux/gui/MyProxy.Linux.Gui.csproj -c Release
dotnet test linux/MyProxyLinux/tests/MyProxy.Linux.Tests.csproj -c Release
~~~

Running the core requires a Linux Xray of the matching version; how to obtain and package it is described in the [Linux README](../linux/MyProxyLinux/README.en.md).

## 5. Verify and distribute

~~~text
python scripts/release_verify.py check
python -m unittest scripts.test_release_verify -v
python scripts/test_package_delivery.py
python -m unittest scripts.test_package_linux -v
python -m unittest discover -s server/tests -v
~~~

Production packaging requires a valid, configured server entry point. Installer signing and update trust keys are managed by the deployer; new installs have automatic update checks off by default, and an existing explicit preference is kept. Published updates must pass signature and digest verification, and installation is confirmed by the user. Keep the third-party licenses, and settle the project-level license before arranging public distribution.
