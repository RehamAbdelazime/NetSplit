# NetSplit Deployment Checklist

Run in order, elevated. Nothing here starts automatically - NetSplit.Service
only observes and reports driver state (see DriverHostService), it never
starts the driver itself.

1. **Build driver**
   `MSBuild NetSplit.driver\NetSplit.driver.vcxproj /p:Configuration=Debug /p:Platform=x64`
   → produces `NetSplit.driver\x64\Debug\NetSplit.driver.sys` (test-signed).

2. **Install driver service**
   `sc create NetSplit type= kernel binPath= "<full path to NetSplit.driver.sys>" start= demand`

3. **Start driver**
   `sc start NetSplit`

4. **Verify device exists**
   Open `NetSplit.App` → Diagnostics (dev) → confirm `CreateFile Succeeded = True`,
   `Device Object Exists = True`, `SCM State = Running`.

5. **Start NetSplit.Service**
   `sc create NetSplit-Svc binPath= "<path to NetSplit.Service.exe>" start= demand`
   `sc start NetSplit-Svc` (or run `NetSplit.Service.exe` directly for a dev session).

6. **Start NetSplit.App**
   Run `NetSplit.App.exe` (no elevation required - it only talks to
   NetSplit.Service over the named pipe).
