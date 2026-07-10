using System.Diagnostics;
using System.Net;
using NetSplit.Driver.Interop;

Console.WriteLine("NetSplit driver runtime-rule IOCTL demo.");
Console.WriteLine("Requires the driver to be loaded and this process elevated.");
Console.WriteLine();

int selfPid = Environment.ProcessId;
var testAddress = IPAddress.Parse("192.168.1.250");

Console.WriteLine($"AddRuntimeRule: this process (PID {selfPid}) -> {testAddress}");
Console.WriteLine(DriverClient.AddRuntimeRule(selfPid, testAddress) ? "  OK" : "  FAILED (is the driver loaded?)");

Console.WriteLine();
Console.WriteLine($"RemoveRuntimeRule: PID {selfPid}");
Console.WriteLine(DriverClient.RemoveRuntimeRule(selfPid) ? "  OK" : "  FAILED");

Console.WriteLine();
Console.WriteLine("RemoveRuntimeRule again (should report not-found / false - already removed):");
Console.WriteLine(DriverClient.RemoveRuntimeRule(selfPid) ? "  unexpectedly OK" : "  OK (correctly not found)");

Console.WriteLine();
Console.WriteLine("ClearRuntimeRules:");
Console.WriteLine(DriverClient.ClearRuntimeRules() ? "  OK" : "  FAILED");
