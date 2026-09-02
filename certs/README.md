# Hub TLS certificates

Place the Hub certificate at `certs/certificate.pfx`. The PFX contains a private key and is intentionally ignored by Git.

The Windows and Linux Hub build scripts copy it to the corresponding `Build` output directory as `certificate.pfx`.

Export the public certificate as `certs/certificate.cer`. The server-plugin build script copies this public-only
certificate into `Build/ServerPlugin`, where the CrossRagfair SPT plugin loads it for SHA-256 certificate pinning.
