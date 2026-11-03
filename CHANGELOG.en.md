# Changelog

This file records changes to public interfaces, behaviour and compatibility. Version numbers are kept consistent across Windows, Android, Linux, the server and the release manifest.

## 0.1.0 — not yet released

- Windows, Android and Linux clients, plus the Python device control plane and 3x-ui integration.
- Pairing, the connection switch, smart routing and global mode, system proxy restoration and device revocation.
- deployment.json as the single server entry point with optional connectivity check endpoints; standard certificate verification for production HTTPS.
- Connectivity checks against your own server, timeout and cancellation handling, a lost-daemon state, and stop and retry after the kill switch engages.
- MIT project license, with third-party licenses, asset digests, native source directions and rebuild/install instructions included.
- Download page template for all three platforms, build regression CI, and contribution and security reporting processes.

Android has passed the existing on-device tests. Later interaction changes in this version still need to be re-verified on real devices; Linux desktop integration needs to be confirmed in the target desktop environments. The first CI and code scanning results on GitHub are to be confirmed after the repository goes public.
