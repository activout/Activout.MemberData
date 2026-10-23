# Activout.MemberData

`member` downloads your own LinkedIn data (connections, messages, invitations, positions, skills and more) to your
computer through LinkedIn's official
[Member Data Portability API](https://learn.microsoft.com/en-us/linkedin/dma/member-data-portability/member-data-portability-member/).
You can then search it yourself, or let an AI agent such as Claude Code or Codex answer questions about it.

It only reads. It never changes your LinkedIn account and never asks for your LinkedIn password. It works only for
**LinkedIn members in the EEA and Switzerland**. This project is independent and **not affiliated with or endorsed
by LinkedIn**.

## Get started

### 1. Download

Get the file for your computer from the [latest release](https://github.com/activout/Activout.MemberData/releases/latest).
The files are not code signed yet, so your computer warns you the first time.

- **Windows:** download `member-win-x64.exe`, rename it to `member.exe` and open PowerShell in that folder. If
  Windows says "Windows protected your PC", click **More info**, then **Run anyway**. Type `.\member.exe` where the
  steps below say `member`.
- **Mac (M1 or newer):** download `member-osx-arm64.tar.gz`, then in Terminal:
  ```sh
  cd ~/Downloads
  tar -xzf member-osx-arm64.tar.gz
  xattr -d com.apple.quarantine member
  sudo mv member /usr/local/bin/
  ```
- **Linux:** download `member-linux-x64.tar.gz`, run `tar -xzf member-linux-x64.tar.gz` and move `member` to a folder
  on your `PATH`.

`member` runs in a terminal. Double-clicking it only flashes a window.

### 2. Get a LinkedIn token

LinkedIn hands out this data only to a developer app of your own. Creating one is free and takes about ten minutes:
follow [docs/dma-setup.md](docs/dma-setup.md). You end up with a long access token.

### 3. Store the token

```sh
member auth import-token --expires-in-days 365
```

Paste the token at the prompt. It is hidden while you paste, checked with LinkedIn, and stored in your computer's
keychain. Never paste it anywhere else, including chats and AI agents. On a Mac, choose **Always Allow** if asked
whether `member` may use the keychain item.

### 4. Download your data

```sh
member sync
```

The first run takes about a minute. Run `member sync` again at least once a week: LinkedIn keeps recent activity
(likes, comments, messages) for only 28 days.

### 5. Look at it

```sh
member domains list                              # what kinds of data you have
member data list --domain CONNECTIONS --limit 20
member skill install                             # let Claude Code, Codex and others answer questions about it
```

Then ask your agent, for example "Who do I know at Spotify?". Remember that an agent sends what it reads to its
model provider, and your data includes messages and information about other people.

That's all most people need. The rest of this page is reference.

---

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

```sh
member data query "SELECT domain, count(*) AS n FROM snapshot_records WHERE deleted_at IS NULL GROUP BY domain"
```

## Coding agents

[`skills/member/SKILL.md`](skills/member/SKILL.md) teaches agents to answer questions with `member`: offline first,
JSON output, SQL over `snapshot_records`, asking before syncing. It is built into `member`:

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

## Verifying a download

Each release has a `SHA256SUMS` file and a build provenance attestation:
`gh attestation verify <file> -R activout/Activout.MemberData`.

## Documentation

- [LinkedIn setup](docs/dma-setup.md): developer app, product, scope, token generation
- [Troubleshooting](docs/troubleshooting.md): exit codes and common errors
- [API findings](docs/api-findings.md): what the API actually does, verified against a live account
- [Storage and sync](docs/storage-and-sync.md): tables, record identity, reconciliation, changelog, 28-day gaps
- [Security and privacy](docs/security-and-privacy.md): secret storage, environment opt-in, data handling
- [Live testing](docs/live-testing.md): how the real-account test was run, and its results

## Building

```sh
dotnet test                               # offline, synthetic fixtures only
dotnet pack -c Release                    # writes artifacts/*.nupkg
./scripts/verify-pack.sh                  # installs the packed tool into a temp dir and runs it
dotnet publish src/Activout.MemberData.Cli -c Release -r osx-arm64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
```

## Releasing

Push a tag such as `v1.1.0` (or `v1.1.0-rc.1` for a prerelease). The Publish workflow tests, packs, builds the three
single-file executables on their own operating systems, attests build provenance, pushes both packages to NuGet.org
via trusted publishing and creates the GitHub release with the executables and `SHA256SUMS`.

## Other ways to get your data

The download above suits most people. One of these may fit you better:

- **LinkedIn's own export:** no app or token needed. Under **Settings → Data privacy → Get a copy of your data**,
  LinkedIn emails you a ZIP of CSV files within a day. It suits a one-off copy; `member` suits keeping an up-to-date
  copy you can query.
- **[.NET tool](https://www.nuget.org/packages/Activout.MemberData.Cli):** if you have the
  [.NET 10 SDK](https://dotnet.microsoft.com/download), `dotnet tool install --global Activout.MemberData.Cli` gives
  the same `member` command, and `dotnet tool update` keeps it current.
- **[.NET library](https://www.nuget.org/packages/Activout.MemberData):** `dotnet add package Activout.MemberData`
  gives typed access to the Member Snapshot, Changelog and Authorization APIs with raw JSON preserved and no
  database, for building your own program.

## Licence

MIT. See [LICENSE](LICENSE).
