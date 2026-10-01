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
- **Connection codes** — how they are generated, stored (DPAPI) and shown.
- **Self-update** — downloading releases and verifying their SHA-256 checksums.
- **Mods, modpacks and backups** — installing archives and modpacks, restoring backups: anything that could write
  outside the folders it should, or delete something it should not.

Out of scope: Vintage Story itself, the code of third-party mods, the ModDB website, and attacks that need
administrator access to the computer running eViSTool.
