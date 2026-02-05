# steam workshop downloader

Console app that downloads Steam Workshop items via SteamPipe depots using SteamKit2.  
It supports filtering, incremental updates (hash-based reuse), and batch processing.

## Features

- Optional file filtering (case-insensitive glob).
- Hash-based reuse of unchanged files (SHA-1 from manifest).
- Atomic folder swap to keep output consistent.
- Batch mode from a text file (one ID per line).
- Rate-limited metadata lookup: 1 request / 2 seconds.
- Logs to stdout/stderr by default; optional log file.
- Refresh-token cache to avoid repeated 2FA prompts.

## Requirements

- .NET 8 SDK/runtime
- Steam account with access to the game

## Usage

Single ID (still uses batch pipeline internally):

```bash
steam-workshop-downloader <publishedFileId> <outputDir> [--appid <id>] [--user <u>] [--pass <p>] [--filter <glob>] [--log <path>] [--auth-cache <path>]
```

Batch from file:

```bash
steam-workshop-downloader <idListFile.txt> <outputDir> [--appid <id>] [--user <u>] [--pass <p>] [--filter <glob>] [--log <path>] [--auth-cache <path>]
```

Alternative positional form:

```bash
steam-workshop-downloader <user> <pass> <outputDir> <publishedFileId or idListFile.txt> [--appid <id>] [--filter <glob>] [--log <path>] [--auth-cache <path>]
```

### Output directory behavior

`outputDir` is always the parent directory.  
Each workshop item is written into a subfolder named after its ID:

```
<outputDir>/<id>/
```

## Examples

Single mod with filters:

```bash
steam-workshop-downloader 2664422411 C:\mods\xcom2 --user myuser --pass mypass ^
  --filter src/**.uc --filter src/**.uci --filter config/**.ini --filter localization/**.int
```

Batch file:

```bash
steam-workshop-downloader C:\mods\ids.txt C:\mods\xcom2 --user myuser --pass mypass --filter src/**.uc
```

`ids.txt` example:

```
# one workshop ID per line
2664422411
2685001234
```

## Filters

Filters are case-insensitive and use `/` as a path separator internally.

Rules:
- `*` matches within a single path segment.
- `**` can span path separators.
- Patterns without `/` are treated as `**/pattern` (match anywhere).

Examples:

- `src/**.uc` matches all `.uc` under `src/` at any depth.
- `config/*.ini` matches only `config/` top-level `.ini`.
- `**/*.xcommod` matches anywhere.

If at least one `--filter` is provided, only matching files are kept.

## Batch pipeline details

- Metadata is fetched via `GetPublishedFileDetails` at most once per 2 seconds.
- Each request can include up to 100 workshop IDs.
- IDs are enqueued as soon as metadata is available.
- Downloads are sequential, but metadata lookup and download run concurrently.

## Logging

- Default: stdout/stderr only.
- Optional: `--log <path>` or env `STEAM_LOG` / `STEAM_WORKSHOP_DOWNLOADER_LOG`.

## Auth cache

Refresh token is cached to avoid repeated 2FA prompts.

Default path:
- Windows: `%APPDATA%\steam-workshop-downloader\auth.json`
- Fallback: `~/.steam-workshop-downloader/auth.json`

Override with:
- `--auth-cache <path>` or env `STEAM_AUTH_CACHE`

## Environment variables

- `STEAM_USER`, `STEAM_PASS`
- `STEAM_GUARD`, `STEAM_EMAIL_GUARD`
- `STEAM_AUTH_CACHE`
- `STEAM_LOG` / `STEAM_WORKSHOP_DOWNLOADER_LOG`

## Build

```bash
dotnet publish -c Release -r win-x64
```

## macOS Gatekeeper workaround

If macOS blocks each library after you allow the main binary, it is because the downloaded
archive is tagged with the quarantine attribute. Remove that attribute from the extracted
folder to trust all embedded binaries at once:

```bash
xattr -dr com.apple.quarantine /path/to/steam-workshop-downloader-osx-arm64
```
