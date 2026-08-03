# Changelog

All notable changes to this project are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/).

The release job extracts the section matching the pushed tag (`## [v0.x.y]`)
and uses it as the GitHub release notes; if no section exists it falls back to
auto-generated notes.

## [Unreleased]

### Added
- Per-domain endianness: optional `endianness` param on all memory tools
  (default `auto` = the domain's native endianness, e.g. Z80 RAM little vs
  68K RAM big on Genesis); every read returns the endianness actually used.
- Watchpoint context dump (`bizhawk_watchpoint_wait context_bytes`): registers +
  PC/disasm + raw bytes around the hit address in one call.
- Fixture capture (`bizhawk_start_fixture`): scripted input timeline + per-frame
  samples written straight to CSV on the host disk.
- Struct reads (`bizhawk_read_struct`): relative-offset fields from a base
  address or symbol, per-field endianness.
- `bizhawk://read/{domain}/{start}:{end}` resource template for raw memory reads.
- Symbols persist across restarts, scoped per ROM hash + namespace
  (`symbols_set/list/clear` accept `namespace`; `get_info` reloads on ROM change).

### Fixed
- `read_many`/`search_memory` ignored configured endianness (little-endian
  reads) — both now resolve the effective endianness.
- Inert watchpoints: `MemoryCallbackImpl.AddressMask` was `null`, so
  address-specific watchpoints never fired on the real core.
- `wait_until` now accepts a symbol `name` (not just a raw address).

## [v0.1.0] - 2026-08-02

### Added
- Placeholder — preencha as mudanças desta versão antes do primeiro release.
  (Baseline: core MCP server, memory read/write/search, symbols, watchers,
  watchpoints, trace, save/load, screenshot, overlays, movies, userdata.)
