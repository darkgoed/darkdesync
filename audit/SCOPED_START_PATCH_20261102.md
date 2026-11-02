# Scoped START patch — 2026-11-02

Scope: DarkDesync only; five strict profiles. No user-PC reproduction is claimed.

## Root cause and correction

- START previously refused the GLOBAL + scoped-profile combination only in the collapsed log. With a selected PID, selecting a strict profile (or selecting the PID afterward, or pressing START after reselecting GLOBAL) now changes only the UI filter to `Conexões verificadas da aplicação`. It stops the old capture, starts PID-only FLOW metadata observation, never saves settings automatically, and requires explicit START for NETWORK.
- No PID: visible instructions outside the diagnostic expander. Experimental reordering: visible session-only opt-in preflight. Disabled checkbox: visible explanation; no pulse.
- START with no eligible tuples: `AGUARDANDO`, STOP cancels, NETWORK remains false. Existing discovery automatically opens the exact tuple filter when eligible FLOW evidence arrives. Manual pulse/hotkey cannot apply an effect while waiting. TCP-only capture explicitly says no UDP is included.
- Driver open failures expose Win32 codes; failure revokes capture request and Armed state. Native exceptions roll back instead of leaving a misleading pending capture. FLOW startup errors retain the native code. Filter replacement clears its reconfiguration flag even on exceptions.
- Four favorites retain GLOBAL outbound behavior. No global inbound path, broad fallback, scheduler/rate changes, consent persistence, or anti-cheat interaction was added.

## START matrix / automated coverage

| Initial state | Action | Expected result | Coverage |
|---|---|---|---|
| GLOBAL, no PID, each new profile | Select + START | Visible PID/scope instructions, START, no capture | ScopedStartTests + WPF visual tree |
| GLOBAL, PID already selected | Select each new profile | UI verified scope only; settings unchanged, no NETWORK | ScopedStartTests + native VM driver test |
| GLOBAL, new profile first | Select PID | Same UI-only migration, no automatic capture | ScopedStartTests |
| Experimental, PID selected, no consent | START | Opt-in message; capture not requested | Unit + native VM driver test |
| Strict live PID, no flows yet | START / manual pulse | AGUARDANDO + STOP; no effect, false NETWORK | Unit + native VM driver test |
| Waiting PID receives eligible own UDP flow | Discovery | Automatic exact-tuple NETWORK, Armed without effect | Native VM driver test, five profiles |
| Armed / Habilitado off then on | Manual pulse | Off: no effect + instruction; on: timed selected pulse | Native VM driver test |
| Strict driver failure (Win32 5), including retry | START | Visible code, Idle, START, request revoked, exact filter only | Deterministic failure seam, five profiles |
| Dead PID | START | Visible unavailable target, no waiting/capture | ScopedStartTests |
| Pending / active | STOP | Request revoked, no capture; queued packets drained | Unit + native VM driver/scheduler suites |
| Four favorites | START / hotkey / pulse / STOP | Existing behavior and durations preserved | Existing unit, WPF, GLOBAL driver regression suites |

## Validation boundaries and release gate

Local Linux cross-build checks compilation only. Windows workflow must pass unit/regression, WPF bindings/visual tree, dependency audit and x64 payload generation. A separate workflow_dispatch with driver_tests=true and legacy_global_tests=true must pass on the PR head and again on the merged main SHA before publishing. Driver tests generate only owned VM loopback traffic. Native non-loopback inbound and the user's Windows installation are not exercised. WPF coverage is in-process visual-tree/control binding automation, not a physical keyboard or desktop robot claim.

Release evidence records exact run URLs, source SHA, TRX counts, payload hashes and readback verification. Previous release remains intact for rollback. Workaround on the previous release: select a live target, choose verified application connections, enable the profile (and experimental consent if applicable), press START, then reconnect owned traffic to establish fresh UDP FLOW evidence.
