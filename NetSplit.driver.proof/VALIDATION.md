# Validation Procedure — Does bind-rewrite at `ALE_BIND_REDIRECT_V4` force adapter selection?

This procedure validates `NetSplit.driver.proof` only. It does not modify,
optimize, or redesign that driver, and it does not touch the production
architecture. Its sole output is a PASS/FAIL determination, with evidence,
for one question:

> Is rewriting `FWPS_BIND_REQUEST0.localAddressAndPort` at
> `FWPM_LAYER_ALE_BIND_REDIRECT_V4` sufficient, by itself, to force a
> process's traffic onto a different physical network adapter?

---

## 1. Environment preparation

1. Close `chrome.exe` completely (all processes, including background/
   updater processes - `Get-Process chrome | Stop-Process -Force`) before
   starting. A running Chrome instance pools/reuses existing sockets;
   testing against a cold start avoids that confound (see §7.3).
2. Have available, elevated:
   - A kernel debug output viewer (Sysinternals **DebugView**, run as
     Administrator, with "Capture Kernel" enabled) - this is the only way
     to observe the proof driver's `DbgPrintEx` output, since it has no
     IOCTL/device surface by design.
   - `netstat`, `netsh`, `route`, `ipconfig`, PowerShell's `NetTCPIP`
     module (`Get-NetAdapter`, `Get-NetIPAddress`, `Get-NetRoute`,
     `Get-NetIPInterface`) - all built in.
   - A packet capture tool bound to a specific adapter (Wireshark, or
     Microsoft's built-in `pktmon`).
3. Do **not** change the routing table, adapter metrics, or weak/strong
   host settings for this test. The whole point is to prove the mechanism
   works using only what already exists.

## 2. Expected adapter configuration (baseline, captured on this machine)

Captured immediately before writing this procedure, via the exact
commands in §3 - **re-run these yourself immediately before testing**;
DHCP leases and adapter state can change between sessions.

| Adapter | ifIndex | IPv4 | Prefix | Interface Metric | Weak Host Send/Receive |
|---|---|---|---|---|---|
| Ethernet (Realtek Gaming 2.5GbE) | 9 | `192.168.1.2` | /24 | 35 | Disabled / Disabled |
| Wi-Fi 2 (Realtek 8822CE) | 14 | `10.118.5.79` | /24 | 55 | Disabled / Disabled |

**Expected Ethernet IP:** `192.168.1.2`
**Expected Wi-Fi IP:** `10.118.5.79`

Both adapters have Weak Host Send/Receive **Disabled**, confirming this
machine is in the strong-host default (Windows Vista+ default) - the
precondition the entire mechanism depends on (see the prior WFP
documentation review). If either shows `Enabled` when you re-run this,
stop - the strong-host constrained-lookup mechanism the proof relies on
is not in effect, and a failure would be inconclusive, not informative.

## 3. Expected routing table

| Destination | Interface | Next Hop | Route Metric | Interface Metric | Effective Metric |
|---|---|---|---|---|---|
| `0.0.0.0/0` | Ethernet (9) | `192.168.1.1` | 0 | 35 | **35 (lower - wins unconstrained lookup)** |
| `0.0.0.0/0` | Wi-Fi 2 (14) | `10.118.5.241` | 0 | 55 | 55 (higher) |

**This is important and deliberate.** Ethernet has the lower effective
metric. That means **without the proof driver active, chrome.exe's
traffic should normally egress via Ethernet** (`192.168.1.1` gateway),
not Wi-Fi - Windows' own unconstrained route lookup prefers Ethernet. The
proof driver's job is to force traffic onto Wi-Fi *despite* Ethernet
being the metric-preferred route. This makes the test meaningful: if
Wi-Fi already had the lower metric, seeing Wi-Fi traffic afterward
wouldn't prove the driver did anything - Windows might have picked it
anyway.

## 4. Verification commands

Run all of these **before** starting the proof driver (baseline) and
**again while chrome.exe is connected with the driver active** (post),
and diff the two.

### PowerShell

```powershell
Get-NetAdapter | Select-Object Name, InterfaceDescription, ifIndex, Status, MacAddress | Format-Table -AutoSize

Get-NetIPAddress -AddressFamily IPv4 | Select-Object InterfaceAlias, IPAddress, PrefixLength | Format-Table -AutoSize

Get-NetRoute -AddressFamily IPv4 | Where-Object { $_.DestinationPrefix -eq "0.0.0.0/0" } |
    Select-Object InterfaceAlias, ifIndex, DestinationPrefix, NextHop, RouteMetric | Format-Table -AutoSize

Get-NetIPInterface -AddressFamily IPv4 | Select-Object ifIndex, InterfaceAlias, InterfaceMetric, WeakHostSend, WeakHostReceive | Format-Table -AutoSize

# Per-adapter traffic counters, sampled twice a few seconds apart during the test -
# confirms which physical adapter actually moved bytes, independent of netstat/capture.
Get-NetAdapterStatistics | Select-Object Name, ReceivedBytes, SentBytes | Format-Table -AutoSize
```

### netsh

```
netsh interface ipv4 show interfaces
netsh interface ipv4 show config
netsh interface ipv4 show route
netsh interface ipv4 show interface "Wi-Fi 2"
netsh interface ipv4 show interface "Ethernet"
```
(`show interface "<name>"` includes the weak/strong host settings in
older Windows builds where `Get-NetIPInterface` may not show them
directly - cross-check both.)

### route print

```
route print -4
```
Confirms the same `0.0.0.0/0` entries as `Get-NetRoute` above, from the
classic tool - useful as an independent cross-check that PowerShell's
`NetTCPIP` module isn't showing stale/cached data.

### ipconfig

```
ipconfig /all
```
Confirms adapter IPs, DHCP lease state, and default gateways match §2/§3
exactly.

### netstat

```
netstat -ano -p TCP | findstr <chrome PID>
```
Run once chrome.exe has an established connection. The **local address**
column for each `ESTABLISHED` line is the socket's actual bound source
IP - this is the single most direct confirmation of "did the socket bind
to Wi-Fi." Get the PID first via:
```
Get-Process chrome | Select-Object Id, ProcessName
```
Chrome runs many processes (browser, renderer, GPU, network service) -
the one making outbound connections is usually the **network service**
utility process, not the main browser PID. Check all `chrome.exe` PIDs'
connections, not just the first one.

## 5. Packet capture recommendations

`netstat` proves what the socket *believes* its local address is; a
packet capture proves what actually left the machine, on which physical
wire. Both are needed - see §7 for why they can disagree.

- **Wireshark**: capture separately and simultaneously on the Wi-Fi
  adapter and the Ethernet adapter (two capture windows, one per
  interface - Wireshark lets you pick the capture interface explicitly).
  Filter: `ip.addr == <destination IP chrome is connecting to>`. A
  successful redirect shows the TCP handshake appearing in the **Wi-Fi**
  capture and **not** in the Ethernet capture.
- **pktmon** (built into Windows 10/11, no install needed):
  ```
  pktmon start --etw -p 0 -c
  pktmon stop
  pktmon format PktMon.etl -o pktmon.txt
  ```
  Slower to filter than Wireshark but useful if Wireshark isn't
  available; `pktmon filter add` can scope it to one adapter by its
  component ID (`pktmon list` shows adapter component IDs).
- Confirm which adapter physically saw the SYN, not just which one saw
  *any* traffic - background Windows telemetry, NTP, DNS-over-HTTPS, etc.
  can generate incidental traffic on either adapter regardless of this
  test, so match on the specific destination IP/port chrome is
  connecting to.

## 6. How to verify each layer

| Question | How to answer it |
|---|---|
| Did the driver actually rewrite the bind request? | DebugView output containing `NetSplitProof: rebound to <Wi-Fi IP>` at the moment chrome.exe opens the connection. This is the *only* driver-internal evidence available (no IOCTL exists to query it) - it comes directly from the `DbgPrintEx` call already in `NetSplitCallout.cpp`'s classify function, on the success path only. |
| Did the socket actually bind to the Wi-Fi source IP? | `netstat -ano` local-address column for that PID/connection, cross-checked against §2's Wi-Fi IP. |
| Which physical adapter carried the traffic? | Packet capture (§5) or `Get-NetAdapterStatistics` byte-count deltas during the connection window, cross-checked against which adapter's capture actually shows the destination IP/port. |
| Was the source IP itself correct end-to-end? | All three of the above must agree: DebugView says rewrite happened → netstat shows the rewritten IP as local address → capture shows that traffic physically on the Wi-Fi wire. Any disagreement between these three is itself a finding (see §8). |

## 7. Distinguishing driver failure vs. Windows routing behavior vs. application behavior

Three different fault domains produce different combinations of the §6
evidence. Work through them in this order - don't skip to routing/app
explanations before ruling out the driver itself, and don't blame the
driver before ruling out the application.

### 7.1 Driver failure

Symptom: **no** `NetSplitProof: rebound to...` line ever appears in
DebugView, despite chrome.exe actively opening new connections during the
capture window.

Before concluding this is a driver bug, rule out:
- The connection was IPv6, not IPv4 - this proof driver only registers at
  `FWPM_LAYER_ALE_BIND_REDIRECT_V4` (see the earlier WFP documentation
  review, item 13). Check the capture: was the destination even IPv4?
  Chrome prefers IPv6 via Happy Eyeballs when available.
- The connection was UDP/QUIC (HTTP/3), not TCP - the callout explicitly
  checks `FWPS_FIELD_ALE_BIND_REDIRECT_V4_IP_PROTOCOL == IPPROTO_TCP` and
  returns immediately for anything else. Modern Chrome uses QUIC by
  default for many sites.
- The socket was reused from before the driver was started (see §7.3).
- `NetSplitProof` service didn't actually reach `Running` state, or
  `NetSplitAdapterDiscovery_FindWiFiAndEthernet` failed at load (check
  DebugView for `NetSplitProof: loaded...` at driver start - if that line
  itself never appeared, or appeared with an error instead, nothing after
  it is meaningful).

If none of these apply and a genuine IPv4 TCP connection from
`chrome.exe` produced no rewrite log line at all → **driver failure**:
the callout isn't matching or isn't being invoked for traffic it should
be seeing.

### 7.2 Windows routing behavior (mechanism insufficiency)

Symptom: DebugView **does** show `rebound to <Wi-Fi IP>`, and `netstat`
**does** show the Wi-Fi IP as the connection's local address, but the
packet capture shows the traffic physically left via Ethernet (or
doesn't appear on the Wi-Fi capture at all).

This means the rewrite reached the socket layer correctly, but the
downstream IP-layer routing decision (the strong-host constrained lookup
described in the earlier documentation review) did not pin the next-hop
interface to Wi-Fi despite the source address being Wi-Fi's. Since §2
already confirmed both adapters have strong host (weak host disabled),
this would be a genuine, surprising finding - re-verify §2/§3 haven't
changed between baseline and test before concluding this, since a
mid-test change to weak-host settings or route metrimtrics would produce
exactly this symptom without it being a driver or "OS design" problem at
all.

If confirmed and reproducible → **bind rewrite alone is insufficient on
this configuration**; something beyond the WFP layer (routing policy,
NDIS-level binding, some third-party network filter, VPN client
interference) overrides the source-address-based interface selection.

### 7.3 Application behavior (confound, not a finding either way)

Symptom: inconsistent results between runs, or a connection that should
have matched didn't, or traffic appears from a `chrome.exe` PID whose
connections don't correspond to what you expected to test.

Known Chrome-specific confounds to control for:
- **Connection pooling/reuse**: an already-open keep-alive socket to a
  site chrome.exe visited *before* the driver started will keep using
  its original (pre-rewrite) binding for the life of that TCP connection
  - bind-redirect only affects the bind() call, which already happened.
  Always fully close and restart chrome.exe after starting the proof
  driver, before testing.
- **Multiple processes**: Chrome's network stack runs in a separate
  utility process from the main window process; `Get-Process chrome`
  returns many PIDs, only one or two of which make outbound connections.
- **IPv6/QUIC preference**: see §7.1 - use a plain HTTP/1.1-or-2-over-TCP,
  IPv4-only test target if possible (e.g. `curl.exe -4 --http1.1
  http://example.com` run *as a separate, simpler control test* alongside
  chrome.exe) to get a clean, deterministic TCP/IPv4 connection that
  isn't subject to Chrome's own protocol-selection behavior. If the
  control test (`curl`) redirects correctly but chrome.exe doesn't, the
  proof driver mechanism is validated and the remaining problem is
  Chrome's own protocol choice, not the driver - report these separately.

None of these are "driver bugs" or "routing insufficiency" - they are
reasons a specific test run's data shouldn't be trusted, and the run
should be repeated with the confound controlled for.

---

## 8. PASS/FAIL matrix

Columns: **R** = driver rewrote (DebugView), **S** = socket shows Wi-Fi IP
(`netstat`), **T** = physical Wi-Fi egress confirmed (capture/adapter
stats).

| R | S | T | Verdict | What it proves |
|---|---|---|---|---|
| Y | Y | Y | **PASS - Proof confirmed** | Bind-rewrite at `ALE_BIND_REDIRECT_V4` is sufficient, by itself, to force this process's traffic onto the Wi-Fi adapter despite Ethernet having the lower route metric. |
| Y | Y | N | **FAIL - Bind rewrite alone is insufficient** | The rewrite reached the socket correctly (local IP is right) but the physical interface selection didn't follow it. See §7.2 - re-verify strong-host/routes weren't changed mid-test before treating this as final. |
| Y | N | N | **FAIL - Rewrite didn't take effect on the socket** | The driver believes it rewrote the bind request (log line present) but the actual socket's local address doesn't reflect it. Possible causes: wrong connection correlated (multiple chrome PIDs/connections - re-check you matched the right one), or the modified data was rejected/overridden after `FwpsApplyModifiedLayerData0` returned. Needs a repeat run with tighter PID/connection correlation before concluding anything about the mechanism itself. |
| Y | N | Y | **Inconsistent - investigate, don't conclude** | Physical capture shows Wi-Fi egress but `netstat` disagrees. This combination shouldn't be possible if both are sampled correctly; treat as a measurement error (stale netstat snapshot, wrong PID, timing race between sampling and connection teardown) and re-run rather than drawing a conclusion from it. |
| N | Y | * | **Invalid - drop this run** | A socket can't show the Wi-Fi address without something having bound it there; if the driver log shows no rewrite yet the socket has the Wi-Fi IP anyway, either the connection was to a Wi-Fi-hosted local test target, chrome.exe was manually bound, or you captured the wrong connection. Re-run with a cleaner test target. |
| N | N | Y | **Invalid - drop this run** | Physical capture shows Wi-Fi traffic with no corresponding rewrite or socket evidence - almost certainly a different connection/process's traffic on the same capture (background OS traffic, a different app). Re-scope the capture filter to the exact destination IP/port under test. |
| N | N | N | **Driver failure OR confound - see §7.1/§7.3 before concluding** | No evidence of a rewrite anywhere. First rule out every §7.1 cause (IPv6, QUIC/UDP, stale reused socket, driver never loaded) and every §7.3 cause (wrong PID, pooled connection). If a genuine fresh IPv4/TCP connection from a verified chrome.exe PID still produced nothing → **driver bug**: the callout is not matching/firing for traffic it should see. |

### Reading the matrix in one line each

- **R+S+T all Y** = Proof confirmed - the production driver's mechanism is sound.
- **R+S=Y, T=N** = Bind rewrite alone is insufficient (matches the user-supplied example exactly).
- **R=N, and all confounds ruled out** = Driver bug (matches the user-supplied example exactly).
- **Any combination with S or T = Y but R = N** = invalid data, not a finding - re-run with a tighter capture/PID scope rather than reporting it as evidence either way.
