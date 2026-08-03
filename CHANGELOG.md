# Changelog

All notable changes to this project are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/).

The release job extracts the section matching the pushed tag (`## [vX.Y.Z]`)
and uses it as the GitHub release notes; if no section exists it falls back to
auto-generated notes. A versioned section is only created when a release is cut
on explicit request — otherwise changes accumulate under `## [Unreleased]`.

## [Unreleased]

### Added
- _(nothing yet — next release's changes land here)_

## [v0.1.0] - 2026-08-02

### Added
- Baseline: native MCP server (Streamable HTTP over `HttpListener`), memory
  read/write/search, symbols, watchers, watchpoints, trace, save/load,
  screenshot, overlays, movies, userdata.
- Per-domain endianness: optional `endianness` param on all memory tools
  (default `auto` = the domain's native endianness, e.g. Z80 RAM little vs
  68K RAM big on Genesis); every read returns the endianness actually used.
- Watchpoint context dump (`bizhawk_watchpoint_wait context_bytes`): registers +
  PC/disasm + raw bytes around the hit address in one call.
- Fixture capture (`bizhawk_start_fixture`): scripted input timeline + per-frame
  samples written straight to CSV on the host disk.
- Struct reads (`bizhawk_read_struct`): relative-offset fields from a base
  address or symbol, per-field endianness.
- Plane decode (`bizhawk_read_plane`): Genesis background nametable (plane A/B)
  + 4bpp tiles + CRAM → PNG (self-contained encoder, exposed as a resource).
- `bizhawk://read/{domain}/{start}:{end}` resource template for raw memory reads.
- Symbols persist across restarts, scoped per ROM hash + namespace
  (`symbols_set/list/clear` accept `namespace`; `get_info` reloads on ROM change).

### Fixed
- `read_many`/`search_memory` ignored configured endianness (little-endian
  reads) — both now resolve the effective endianness.
- Inert watchpoints: `MemoryCallbackImpl.AddressMask` was `null`, so
  address-specific watchpoints never fired on the real core.
- `wait_until` now accepts a symbol `name` (not just a raw address).
- `read_palette` decoded Genesis CRAM with R/B in the wrong bit positions —
  the hardware format is `0x0RRR0GGG0BBB` (R at bits 1-3, B at 9-11).
