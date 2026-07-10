using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetSplit.Core;
using NetSplit.Driver.Interop;
using NetSplit.Ipc;
using NetSplit.Network;
using NetSplit.Runtime;
using NetSplit.Service;

string dataDirectory = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NetSplit");

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options => options.ServiceName = "NetSplit");

builder.Logging.AddProvider(new RollingFileLoggerProvider(Path.Combine(dataDirectory, "Logs")));

// --- Dependency graph (constructor injection only, no static singletons) ---
//
//   Worker (composition root - wires events, drives the tick, nothing else)
//     |-- NetworkDiscoveryService        (NetSplit.Network - stateless discovery)
//     |-- DiscoverySnapshotPump          (implements IProcessMonitor, IAdapterMonitor)
//     |-- RuntimeResolver                (depends on: IRoutingRuleService)
//     |-- RuntimeRuleSynchronizer        (depends on: RuntimeResolver)
//     |-- IDriverHost -> DriverHostService (depends on: ILogger)
//     '-- RoutingRuleIpcServer           (depends on: IRoutingRuleService, DiagnosticsService)
//
//   DiagnosticsService depends on: IRoutingRuleService, IDriverHost, RuntimeResolver
//
// No cycles: everything points only "downward" toward IRoutingRuleService/
// NetworkDiscoveryService, which have no dependencies of their own.
builder.Services.AddSingleton<IRoutingRuleService>(
    _ => new FileRoutingRuleService(Path.Combine(dataDirectory, "rules.json")));
builder.Services.AddSingleton<NetworkDiscoveryService>();
builder.Services.AddSingleton<DiscoverySnapshotPump>();
builder.Services.AddSingleton<RuntimeResolver>();
builder.Services.AddSingleton<RuntimeRuleSynchronizer>();
builder.Services.AddSingleton<IDriverHost, DriverHostService>();
builder.Services.AddSingleton<DiagnosticsService>();
builder.Services.AddSingleton<RoutingRuleIpcServer>(sp =>
    new RoutingRuleIpcServer(
        sp.GetRequiredService<IRoutingRuleService>(),
        statusProvider: () => sp.GetRequiredService<DiagnosticsService>().GetStatus(),
        diagnosticsProvider: uiCount => sp.GetRequiredService<DiagnosticsService>().GetDiagnostics(uiCount)));

builder.Services.AddHostedService<Worker>();

IHost host = builder.Build();
host.Run();
