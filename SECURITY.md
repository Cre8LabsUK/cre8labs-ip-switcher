# Security policy

IP Switcher runs with administrator rights, because Windows requires them to change IP settings. Security problems are therefore taken seriously.

## Reporting a vulnerability

Please **do not** open a public issue. Use GitHub's **Report a vulnerability** button on the Security tab of this repository (private vulnerability reporting), or contact Cre8Labs through [cre8labs.uk](https://cre8labs.uk).

Include the steps to reproduce and the version you tested. You should get a reply within a week.

## Design notes

- `netsh.exe` is always started from the Windows system folder, never from the current or app folder.
- Every value passed to `netsh` is validated as an IPv4 address or mask and rewritten in canonical form first.
- The app never uploads data and has no auto-update. The only network traffic is pings you start yourself.
- Release builds are produced by GitHub Actions from tagged commits, with SHA-256 checksums published alongside.
