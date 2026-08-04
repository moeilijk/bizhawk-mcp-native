# Contributing

Thanks for wanting to help! Before you open an issue or a pull request, please
read this — it will save both of us time.

## The maintenance model (read this first)

This project started as a personal tool to fill a specific need of its author
(agent-driven game research on the BizHawk emulator). It is not a product with
a dedicated maintainer:

- **The author checks the repository sporadically** — sometimes weeks or
  months go by between visits. There is no SLA for replies, reviews, or merges.
- **Fork freely.** The project is MIT-licensed precisely so you can take it,
  adapt it, and run with it without waiting for anyone. If you build something
  on top of it, a small link back is appreciated but not required.
- **Pull requests are welcome** and will eventually be reviewed — just don't
  expect a fast turnaround. If your PR is urgent for you, maintain it in your
  fork in the meantime.

## Opening an issue

Issues are how the author (and other users) learn about what's broken or
missing. A good issue is written so it can be understood months later, with no
back-and-forth:

- **Title**: one line that states the problem or idea (`read_memory
  returns wrong value for u32 on SNES`, not `it's broken`).
- **Body**:
  - What you did (the exact tool call / command).
  - What you expected, and what actually happened (paste the real JSON
    response or error).
  - Environment: OS (Windows / WSL / Linux), BizHawk version or commit, core
    and game if relevant.
  - For feature requests: the use case — *why* you need it, not just *what*
    you want. The author builds for real needs.
- If you found a bug in a tool, include the hex/dec addresses you used
  (compute them with a script, never by hand) and the exact steps to reproduce.

## Opening a pull request

PRs that get merged share these traits:

- **Small and focused.** One concern per PR. Large sweeping refactors are hard
  to review asynchronously and will likely sit for a long time.
- **Explain the why** in the description: the problem you were solving, how
  your change solves it, and anything you couldn't test.
- **Follow the repo conventions**:
  - `net48` only — never introduce a dependency that needs .NET 8+.
  - All emulator API calls go through the UI thread (`_ui.Invoke`).
  - Tools are core-neutral unless the feature is core-specific (then the name
    says so: `genesis_*`).
  - Every hex number in a description, test, or message must be produced or
    verified by a script — hand arithmetic has caused real bugs here.
  - Add a unit test for new tools (tests run without BizHawk: `./scripts/test.sh`).
  - Update the docs: `CHANGELOG.md` (a bullet under `## [Unreleased]`), the
    README tool table, and `AGENTS.md` if the change affects agents.
- **Verify before submitting**: `./scripts/test.sh` green, and if you changed
  the plugin, a Release build with zero warnings
  (`dotnet build src/BizHawkMcp/BizHawkMcp.csproj -c Release`).
- **Don't bump versions or cut releases.** The author does that when cutting
  a release; the changelog convention is documented in `AGENTS.md`.

## A note on how this project is built

Most of this codebase was written by AI agents ("vibe coding", if you like) —
the author directs, reviews, and verifies everything they can against the real
emulator, and treats the final delivery as the standard: clean builds, zero
warnings, tests, docs, and live verification. Don't be surprised to find
agent-flavored prose or the occasional odd comment; do hold the code to the
same bar it holds itself.

## Development setup

See the README's [Setup](README.md#setup-first-time) section: copy
`.env.example` to `.env`, set `BIZHAWK_INSTALL`, and you're ready to build and
test.
