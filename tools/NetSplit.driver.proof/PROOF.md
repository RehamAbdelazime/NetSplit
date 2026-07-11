# NetSplit.driver.proof

An isolated, throwaway kernel driver. It answers exactly one engineering
question:

**Can `ALE_BIND_REDIRECT_V4` redirect a process onto a different adapter by
rewriting the local bind address?**

Nothing else. It is completely separate from the production driver
(`NetSplit.driver`) and everything downstream of it (`NetSplit.Service`,
`NetSplit.Driver.Interop`, `NetSplit.Ipc`, `NetSplit.App`) - none of that is
touched, referenced, or required by this project. This project is not part
of `NetSplit.slnx` and is not meant to become part of it.

## What it does

- Hardcoded target process: `chrome.exe` (`NETSPLIT_PROOF_TARGET_PROCESS`
  in `Driver.cpp` - edit and rebuild to change it).
- At `DriverEntry`, it discovers the machine's current Wi-Fi adapter IPv4
  once (`AdapterDiscovery.cpp`) and hardcodes that address as the rewrite
  target for the rest of the driver's lifetime - no re-resolution, no
  IOCTL, no rule engine, no PID lookup, no runtime rule sync.
- Registers one `ALE_BIND_REDIRECT_V4` callout + filter (own GUIDs, own
  sublayer - cannot collide with the production driver's).
- On every IPv4 TCP bind whose process image path ends in
  `chrome.exe`, rewrites `FWPS_BIND_REQUEST0.localAddressAndPort`'s address
  to the discovered Wi-Fi IPv4. Everything else is permitted unchanged.
- No device control routine, no symbolic link, no SDDL - there is no
  user-mode-reachable surface at all. The device object that exists is
  only there because `FwpsCalloutRegister1` requires one.

## What it does NOT do (by design, not by omission)

- No IOCTL surface of any kind.
- No PID-based rule engine (unlike `NetSplit.driver`, which is exclusively
  PID-based - this proof matches on process image path instead, since
  there is no user-mode caller to hand it a PID).
- No runtime rule synchronization, no Service, no persistence.
- No adapter discovery in the production driver - discovery lives here,
  in this isolated project, only.

## Running the experiment

1. Build: open an elevated prompt and run MSBuild against
   `NetSplit.driver.proof.vcxproj` (Debug|x64), or build it from Visual
   Studio directly. Produces `x64\Debug\NetSplit.driver.proof.sys`
   (test-signed automatically).
2. Install (elevated):
   `sc create NetSplitProof type= kernel binPath= "<full path to NetSplit.driver.proof.sys>" start= demand`
3. Start (elevated): `sc start NetSplitProof`
   - Check `DbgView`/kernel debugger output for
     `NetSplitProof: loaded. chrome.exe will be rebound to <a.b.c.d> (Wi-Fi)...`
   - If it instead logs "could not resolve both a Wi-Fi and an Ethernet
     adapter IPv4", the machine doesn't have both adapter types active
     right now - the proof can't run until it does.
4. Launch `chrome.exe` and make it load something.
5. Confirm the routing: `netstat -ano` while chrome.exe is connected, and
   check the **local** address of its established connections - it should
   show the Wi-Fi adapter's IPv4, not whatever adapter Windows' normal
   routing table would have picked. A packet capture on the Wi-Fi adapter
   showing chrome.exe's traffic (and none on the other adapter) is the
   strongest confirmation.
6. Stop and remove when done (elevated):
   `sc stop NetSplitProof`
   `sc delete NetSplitProof`

## Answer this, then discard

- **YES, traffic visibly egresses via the Wi-Fi adapter** → the mechanism
  is proven; the production driver's approach (same rewrite, same layer,
  PID-based instead of path-based) is sound. Uninstall this driver,
  delete this project, resume the production architecture unchanged.
- **NO, traffic still egresses via the original adapter** → the mechanism
  itself doesn't work on this machine/OS build the way the documentation
  review assumed, and that finding should go back into the architecture
  review before touching `NetSplit.driver` again.

Either way, this project's job ends the moment the question is answered.
It is not meant to accumulate features, tests, or fixes of its own.
