#!/usr/bin/env -S dotnet run
using System.Diagnostics;
var psi = new ProcessStartInfo("dotnet-forge") { UseShellExecute = false };
foreach (var a in new[] { "stop" }.Concat(args)) psi.ArgumentList.Add(a);
var p = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start dotnet-forge");
p.WaitForExit();
return p.ExitCode;