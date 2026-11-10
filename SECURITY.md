# Security policy

## Reporting a vulnerability

Please **do not** report security problems in public issues.
Report them privately: [**Report a vulnerability**](https://github.com/erneywhite/eViSTool/security/advisories/new)
(the Security tab of this repository). Only the maintainer sees the report.

Describe what you found, how to reproduce it and which version of eViSTool you used.
English or Russian are both fine. / Можно писать по-русски.

eViSTool is maintained by one person in their free time. You will get an answer within a week;
a confirmed problem is fixed in a new release as soon as possible, and you are credited in the release notes unless you prefer otherwise.

## Supported versions

Only the [latest release](https://github.com/erneywhite/eViSTool/releases/latest) receives fixes.
eViSTool updates itself from the **About** page, so please check that the problem is still there in the latest version.

## What is in scope

Most interesting are the parts that deal with the network and with files on disk:

- **Remote management** — the agent's remote listener: the TLS connection and certificate pinning, the key check
  and the lockout after wrong keys, the commands it accepts, uploaded mod archives.
- **Secrets at rest** — connection codes, notification tokens and the agent's remote key and certificate. On Windows
  they are encrypted with DPAPI for the current user. On Linux there is no DPAPI: secrets are encrypted with
  AES-256-GCM using a per-user key file readable only by its owner (mode 600), and the agent's key and certificate
  are owner-only files.
- **Self-update** — downloading releases, the SHA-256 checksum, and the ECDSA P-256 signature of every update
  archive. The signing key exists only in the release build on GitHub Actions; the public key is built into the
  program, and an archive without a valid signature, or with a version that does not match the release, is not
  installed.
- **Mods, modpacks and backups** — installing archives and modpacks, restoring backups: anything that could write
  outside the folders it should, or delete something it should not.

Out of scope: Vintage Story itself, the code of third-party mods, the ModDB website, and attacks that need
administrator access to the computer running eViSTool.
