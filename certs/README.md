# Hub TLS certificate

Place the Hub certificate at `certs/certificate.pfx`. The PFX contains a private key and is intentionally ignored by Git.

The Windows and Linux Hub build scripts copy it to the corresponding `Build` output directory as `certificate.pfx`.
