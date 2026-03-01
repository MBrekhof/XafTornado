using System;
using System.Linq;
using System.Reflection;

var asm = Assembly.LoadFrom(@"C:\Users\joche\.nuget\packages\github.copilot.sdk\0.1.23\lib\net8.0\GitHub.Copilot.SDK.dll");
foreach (var t in asm.GetExportedTypes().OrderBy(t => t.FullName))
    Console.WriteLine(t.FullName);
