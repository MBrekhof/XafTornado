using System;
using System.Reflection;
using System.Linq;

var dll = Assembly.LoadFrom(@"C:\Users\joche\.nuget\packages\github.copilot.sdk\0.1.23\lib\net8.0\GitHub.Copilot.SDK.dll");
foreach(var t in dll.GetExportedTypes().OrderBy(t => t.FullName))
{
    Console.WriteLine(t.FullName);
    foreach(var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
    {
        var parms = string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name));
        Console.WriteLine($"  {m.ReturnType.Name} {m.Name}({parms})");
    }
    foreach(var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
    {
        Console.WriteLine($"  prop {p.PropertyType.Name} {p.Name}");
    }
}
