# Map application icon provenance

## Selected owner input

`FACT`: the Stage 11 application icon is the owner-approved, original Map artwork copied
mechanically from the read-only `fix/app-icon` worktree. It is not Inkarnate material and no
third-party asset or brand element was introduced.

| Repository file | Source SHA-256 |
|---|---|
| `src/Map.App/Assets/AppIcon-Large.png` | `8BF8D23D901D005670DE90C34E08DBB0BFA39993C6363E8DE229C64352DED4E1` |
| `src/Map.App/Assets/AppIcon-Large.ico` | `7E396A60DFB2B6AF62807445D62FF5C7E19168EA494F923EB150039F3FDCD4AC` |

Source identity at intake:

- path: `C:\Users\Leshe\.codex\worktrees\fix-app-icon\Map`;
- branch: `fix/app-icon`;
- the source worktree was owner-dirty and was therefore treated as a read-only input;
- only the two exact-hash files and the two related project settings were transferred.

## Resource contract

- PNG dimensions: 1,254×1,254.
- ICO frames: 16, 24, 32, 48, 64, 96, 128 and 256 pixels.
- `Map.App.csproj` sets `ApplicationIcon` to `Assets\AppIcon-Large.ico`.
- Avalonia includes the application icon as the window icon through
  `AvaloniaIncludeApplicationIconAsWindowIcon=true`.

`scripts/check-stage11-icon.ps1` rejects a changed hash, missing required frame, wrong PNG
dimensions or missing project settings. With a published EXE it also extracts the associated
Windows icon and records a hash in ignored evidence. Embedded resource inspection and a fresh
shortcut are required because the Windows icon cache alone is not trustworthy evidence.
