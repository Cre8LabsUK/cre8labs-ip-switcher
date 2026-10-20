# Contributing

Thanks for helping improve Cre8Labs IP Switcher.

## Reporting bugs

Open an issue with the **Bug report** template and include:

- the app version (bottom right of the window)
- your Windows version
- what you clicked and what happened
- the relevant lines from the **Activity** panel (bottom right). They contain the exact message Windows returned.

If the app crashed, attach `%TEMP%\Cre8Labs-IPSwitcher-crash.log`.

Please remove anything you would rather keep private, such as MAC addresses or public IPs.

## Development setup

1. Install the [.NET SDK](https://dotnet.microsoft.com/download) and, for installer work, Inno Setup 6 (`winget install JRSoftware.InnoSetup`).
2. Build a preview that does not ask for admin, for UI work:

   ```powershell
   dotnet build src -c Release -p:NoElevate=true -o preview-build
   ```

   It shows live status but cannot change settings. Use **Restart as admin** in the banner to test real changes.
3. Build everything (app, installer, checksums) with `build.ps1`.

## Pull requests

- Keep changes focused: one fix or feature per pull request.
- Match the existing style: plain code-behind and small view models, no MVVM framework.
- Keep networking logic in `src/Core` and free of UI code.
- New colors and styles belong in `App.xaml`, using the existing brand brushes.
- Build with zero warnings, and check that the app still starts.
- Describe how you tested, especially for anything that runs `netsh`.

## Code of conduct

Be kind and constructive. Harassment or personal attacks are not tolerated.
