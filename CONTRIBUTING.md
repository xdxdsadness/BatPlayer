# Contributing to BatPlayer

Thank you for considering a contribution!

## How to contribute

1. Fork the repository and create a branch from `main`:
   ```bash
   git checkout -b feature/my-feature
   ```
2. Make your change. Keep the existing code style:
   - brief English comments that explain *why*, not *what*;
   - MVVM: no business logic in code-behind;
   - all user-facing strings go through localization (`Resources/Strings*.resx`).
3. Add or update tests for any change in pure logic (`tests/BatPlayer.Tests`).
4. Verify locally:
   ```bash
   dotnet build BatPlayer.sln -c Release
   dotnet test
   ```
5. Open a pull request with a clear description of what changed and why.

## Reporting issues

Please include: Windows version, player version, steps to reproduce, and the log file
from `%LocalAppData%\BatPlayer\logs`.
