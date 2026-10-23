# Activout.MemberData

Download your own LinkedIn data through LinkedIn's official
[Member Data Portability (Member) API](https://learn.microsoft.com/en-us/linkedin/dma/member-data-portability/member-data-portability-member/),
keep it in a local SQLite database, and query it from scripts or coding agents such as Claude Code and Codex.

- **Activout.MemberData** is a .NET 10 library: typed HTTP access to the Member Snapshot, Member Changelog and
  Member Authorization APIs, with raw JSON preserved and no database dependency.
- **Activout.MemberData.Cli** is the `member` command: secure token storage, sync into SQLite, JSON output and
  read-only SQL.

```sh
member auth import-token          # paste the token from LinkedIn's OAuth Token Generator (hidden input)
member sync                       # changelog every time, snapshot when needed
member domains list
member data list --domain CONNECTIONS --limit 20
member data query "SELECT domain, count(*) AS n FROM snapshot_records WHERE deleted_at IS NULL GROUP BY domain"
```

It only reads. It never changes your LinkedIn account, never scrapes, and never asks for your LinkedIn password.
This project is independent and **not affiliated with or endorsed by LinkedIn**.

## Who can use it

The Member Data Portability (Member) product exists because of the EU Digital Markets Act. It is available only to
**LinkedIn members in the EEA and Switzerland**, and each person needs **their own** LinkedIn developer application.
Setting that up takes about ten minutes; [docs/dma-setup.md](docs/dma-setup.md) walks through it.

## Install

### Download a ready-made program (no .NET needed)

Get the file for your computer from the [latest release](https://github.com/activout/Activout.MemberData/releases/latest):

| Computer | File |
| --- | --- |
| Windows (64-bit Intel/AMD) | `member-win-x64.exe` |
| Mac with Apple silicon (M1 or newer) | `member-osx-arm64.tar.gz` |
| Linux (64-bit Intel/AMD) | `member-linux-x64.tar.gz` |

`member` is a command-line program: run it from a terminal (Windows Terminal, PowerShell, macOS Terminal).
Double-clicking it only flashes a window.

The files are **not code signed** yet, so the operating system warns the first time:

- **Windows:** rename the file to `member.exe` and put it in a folder of your choice. If SmartScreen says "Windows
  protected your PC", click **More info**, then **Run anyway**. Run it as `.\member.exe --help` in PowerShell.
- **macOS:** `tar -xzf member-osx-arm64.tar.gz`, then `xattr -d com.apple.quarantine member` (this removes the
  "cannot be opened because the developer cannot be verified" block), then `./member --help`. Move it to a folder on
  your `PATH`, for example `sudo mv member /usr/local/bin/`. macOS may ask once whether `member` may use the
  keychain item it stored; choose **Always Allow**.
- **Linux:** `tar -xzf member-linux-x64.tar.gz && ./member --help`.

Each release has a `SHA256SUMS` file and a build provenance attestation: `gh attestation verify <file> -R
activout/Activout.MemberData`.

### As a .NET tool

With the [.NET 10 SDK](https://dotnet.microsoft.com/download):

```sh
dotnet tool install --global Activout.MemberData.Cli
```

## Quick start

1. Create your LinkedIn developer app and generate a token ([docs/dma-setup.md](docs/dma-setup.md)).
2. Store the token in your operating system's secure store. It is read from a hidden prompt or stdin, never from
   command arguments:
   ```sh
   member auth import-token --expires-in-days 365  # LinkedIn's DMA tokens last 12 months
   member auth status
   ```
3. Synchronise. The first run downloads every snapshot domain (about a minute for a large account):
   ```sh
   member sync
   member sync status
   ```
4. Run `member sync` at least weekly. LinkedIn keeps changelog events (likes, comments, messages and so on) for
   only **28 days**, and `member` warns you as that limit approaches.

## Commands

| Command | What it does |
| --- | --- |
| `member auth import-token [--stdin] [--expires-at T \| --expires-in-days N]` | Verify the token with LinkedIn and store it securely |
| `member auth status` | Credential source, whether a token exists, expiry, what to do (offline) |
| `member auth logout` | Remove the stored token; the database is kept |
| `member auth registration` | Ask LinkedIn whether you are registered for DMA data generation |
| `member auth register` | Ask LinkedIn to start data generation (only if `registration` reports none) |
| `member sync [--full] [--domain D]` | Fetch the changelog, and the snapshot when needed or asked |
| `member sync status` | Checkpoints, recent runs, changelog retention warnings (offline) |
| `member domains list` | Documented and discovered domains with local status (offline) |
| `member data list [--domain D] [--limit N] [--offset N] [--include-deleted]` | Stored snapshot records (offline) |
| `member data get --domain D --id KEY` | One record (offline) |
| `member data query "SELECT …" \| --file q.sql [--max-rows N] [--timeout S]` | Read-only SQL (offline) |
| `member database info` | Path, schema, member binding, counts |
| `member database purge --yes` | Delete the local database |
| `member skill install [--target all\|claude\|agents] [--dir D]` / `member skill show` | Install or print the coding-agent skill |

Global options are `--database PATH`, `--format json|table` and `--credential-source keychain|environment`. Output is
one JSON document on stdout when stdout is redirected (or with `--format json`), and progress goes to stderr.
Exit codes are listed in [docs/troubleshooting.md](docs/troubleshooting.md).

## Using it from a coding agent

[`skills/member/SKILL.md`](skills/member/SKILL.md) teaches agents to answer questions about your LinkedIn data with
`member`: offline first, JSON output, SQL over `snapshot_records`, asking before syncing. The skill is built into
`member`, so installing it needs no repository checkout:

```sh
member skill install                  # both locations below
member skill install --target claude  # only ~/.claude/skills (Claude Code)
member skill install --dir ./.agents/skills   # a project's skills directory, or any other location
member skill show                     # print SKILL.md, e.g. for an agent with another skills folder
```

| Target | Directory | Read by |
| --- | --- | --- |
| `claude` | `~/.claude/skills/member/` | Claude Code (also GitHub Copilot and OpenCode) |
| `agents` | `~/.agents/skills/member/` | Codex (documented user location), and per their docs also Gemini CLI, GitHub Copilot and OpenCode |

Running `member skill install` again after an upgrade updates the skill. It refuses to overwrite a
`member/SKILL.md` it didn't write unless you add `--force`, and it leaves a symlinked `member` folder alone. When
working on this repository, link instead of installing:
`ln -s "$PWD/skills/member" ~/.claude/skills/member && ln -s "$PWD/skills/member" ~/.agents/skills/member`.

Your database contains messages and information about other people. An agent that reads it sends what it reads to
its model provider. Decide which data you let it see, and don't publish what you get back.

## Documentation

- [LinkedIn setup](docs/dma-setup.md): developer app, product, scope, token generation
- [API findings](docs/api-findings.md): what the API actually does, verified against a live account
- [Storage and sync](docs/storage-and-sync.md): tables, record identity, reconciliation, changelog, 28-day gaps
- [Security and privacy](docs/security-and-privacy.md): secret storage, environment opt-in, data handling
- [Live testing](docs/live-testing.md): how the real-account test was run, and its results
- [Troubleshooting](docs/troubleshooting.md): exit codes and common errors

## Building

```sh
dotnet test                               # offline, synthetic fixtures only
dotnet pack -c Release                    # writes artifacts/*.nupkg
./scripts/verify-pack.sh                  # installs the packed tool into a temp dir and runs it
dotnet publish src/Activout.MemberData.Cli -c Release -r osx-arm64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
```

## Releasing

Push a tag such as `v0.1.0` (or `v0.1.0-rc.1` for a prerelease). The Publish workflow tests, packs, builds the three
single-file executables on their own operating systems, attests build provenance, pushes both packages to NuGet.org
via trusted publishing and creates the GitHub release with the executables and `SHA256SUMS`.

## Licence

MIT. See [LICENSE](LICENSE).
