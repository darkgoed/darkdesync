# Issue #37 — removal of the six-experiment laboratory

## Scope and review

Base: b647a3e7a980f538770534dd292ed5d8ef335eb5 (.13).
Single worktree: /home/deploy/darkdesync-clean-panel.
Branch: fix/clean-panel-issue37-20261102.

Removed the separate launcher/window, experiment catalog/report/status/endpoint
models, laboratory capture and codecs, exclusive unit/driver/UI tests, reflector
and its Python test, laboratory documentation and exclusive CI step.
No new effects or controls. SDK default compile inclusion automatically removes
the deleted C# files; csproj has no explicit laboratory entries or dependencies
to remove. Shared WinDivert bindings, binary packaging and existing legacy core
remain unchanged.

Exactly four production favorites remain: Dual Token Bucket, Flow-Aware Shaping,
Outbound Freeze and Bandwidth Shaping. DiagnosticMode and settings are unchanged;
append-only IDs and read-only rejection of removed choices remain covered by
existing Windows unit/WPF/native driver tests. Existing tests cover the 250/800 ms
presets, ratings, compact layout, START/ARMADO/PULSO/STOP, Space routing, driver
errors, global outbound, cancellation and fail-open.

PacketEngine.cs and the dedicated release worker regression assertion are
unchanged from .13: b207565's LongRunning worker and cancellable 4 ms wait are
preserved. This is a scoped deletion, not a revert to .12.

New external-process Windows UIAutomation checks run both without driver and in
the native UI gate. They inspect the real panel for the absent launcher, expand
the actual selector, verify/select all four favorite identities, and verify the
removed window type is absent from the compiled application.

## Verification status

Pending exact-head PR CI and workflow_dispatch with driver_tests=true and
legacy_global_tests=true. Final main requires the same independent gates before
publishing. Run IDs, source SHAs, TRX totals, release hashes and download checks
will be recorded in release evidence; this file does not claim pending runs pass.

## Risks and rollback

GLOBAL pulses affect all outbound traffic on the machine. Driver testing is only
on an isolated elevated Windows Actions runner with generated owned loopback
traffic. No production host, game, physical NIC or user's computer is tested.
Windows scheduling is not hard real-time; STOP prioritizes safe draining.
No Roblox Player/anticheat or physical keyboard claim is made.

Keep .13 and .12 releases/assets intact. Rollback: close the app (STOP drains),
restore the earlier complete ZIP in a separate directory and retain the original
settings JSON; never rewrite legacy settings merely to make a removed mode start.
No host configuration, database or persistent driver policy is changed by this
cleanup. No unrelated worktrees or services are modified.
