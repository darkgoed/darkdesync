# Issue #33 — exactly four approved outbound effects

Base and preserved rollback: 6946b71b738300c21ef8f9d558c7aaf9d386de38 / v2026.11.02-five-inbound-experimental.11.
Scope: isolated worktree darkdesync-remove-seven, branch fix/remove-seven-unwanted-effects-20261102. No primary-worktree, HCC, Roblox, infrastructure or credential changes.

## Implementation and review

ModeCatalog.OutboundChoices is an allowlist containing exactly Dual Token Bucket, Flow-Aware Shaping, Outbound Freeze (ranked 1/2/3) and Bandwidth Shaping. All enum IDs 0–37 and legacy labels remain unchanged. No new effects or techniques.

Existing VM read-only migration now rejects the seven #28/#30 modes as well as previously hidden modes. Loading stops/drains capture, clears the selector, preserves duration and original bytes and blocks START/pulse/Save until a deliberate valid selection. Selection itself does not save; only explicit Save replaces the configuration.

Production MainViewModel installs a one-way approved-only engine policy before any settings load. TriggerArmedEffect, ActiveDiagnosticMode assignment and controller cycles cannot activate a hidden mode on that engine. Internal legacy regression harness keeps legacy algorithms available: the two pre-existing DSCP/temporal save-roundtrip tests now explicitly use the internal nonproduction VM, retaining their algorithm/settings assertions. No tests skipped/removed, no workflow or dependency changes. New production regressions cover seven removed modes × four replacements, byte preservation, direct engine/controller bypass attempts and all hidden enum identities. Existing exhaustive enum × filter settings tests and rejected-eight tests remain.

Windows visual-tree test asserts exact four-element ordered list and tests hidden saved modes including all seven. Existing native external-process UIA loops automatically cover exactly four valid choices and every hidden saved label, blocked START/manual pulse, untouched file, plus native START/STOP, Space-event routing, compact dimensions, pills and presets. Native tests generate only owned loopback traffic. GLOBAL capture still applies to all outbound on the isolated Windows runner; it must never run on shared production hosts.

Reviewed production constructor/selection/load/save, controller and engine pulse admission and cancellation. Packet schedulers, native send/drain, WinDivert and XAML unchanged. Linux host has no dotnet command; actual build/xUnit/WPF/native execution must be proven by exact-SHA Windows Actions, not claimed locally. Dependency security audit remains mandatory in workflow. Branch-protection API returns HTTP 403 (private-repository plan limitation); no bypass authorized or used.

## Gates and release contract

PR Windows CI and workflow_dispatch(driver_tests=true, legacy_global_tests=true) at exact PR head. Merge only after success. Repeat push/main and explicit driver dispatch at final main SHA. Build ZIP only from that successful main artifact; inspect EXE/WinDivert.dll/WinDivert64.sys/SOURCE_COMMIT, generate SHA256 and provenance linking all runs and counts, download published assets and compare hashes. Keep .11 untouched. Close issue only after published-release verification. Actual run IDs/results and final source SHA belong in release provenance, not predictions in this document.

## Risks / exclusions

Saved removed modes intentionally require user selection; they do not silently fall back. Internal legacy regression code remains compiled, but production has no selector/settings activation path to it. GLOBAL WinDivert can disrupt other applications; STOP/drain is fail-open best effort, not a guarantee of zero kernel/network loss. Unchanged fixed presets Legit=250 ms/Agressivo=800 ms, manual ms and personal assessments are not gameplay guarantees.

User PC, physical keyboard, Roblox/anticheat, real WAN and adverse network conditions NOT tested. No injection, bypass, new exploit research or third-party endpoint traffic in this issue. Rollback is the preserved .11 release (which intentionally contains rejected modes); preserve settings backup before manually reverting.
