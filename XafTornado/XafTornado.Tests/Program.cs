using System.IO;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using XafTornado.Tests;
using XafTornado.Tests.Models;

if (args.Length == 0)
{
    Console.WriteLine("XafTornado Test Runner");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run --project XafTornado.Tests -- <test.yaml> [base-url]");
    Console.WriteLine();
    Console.WriteLine("  test.yaml   Path to a YAML test script");
    Console.WriteLine("  base-url    Base URL of the running app (default: http://localhost:5000)");
    Console.WriteLine();
    Console.WriteLine("Examples:");
    Console.WriteLine("  dotnet run --project XafTornado.Tests -- tests/sample-orders.yaml");
    Console.WriteLine("  dotnet run --project XafTornado.Tests -- tests/sample-orders.yaml http://localhost:5001");
    return 1;
}

var scriptPath = args[0];
var baseUrlArg  = args.Length > 1 ? args[1] : null;

if (!File.Exists(scriptPath))
{
    Console.Error.WriteLine($"Error: file not found: {scriptPath}");
    return 1;
}

var deserializer = new DeserializerBuilder()
    .WithNamingConvention(UnderscoredNamingConvention.Instance)
    .IgnoreUnmatchedProperties()
    .Build();

TestScript script;
try
{
    var yaml = await File.ReadAllTextAsync(scriptPath);
    script = deserializer.Deserialize<TestScript>(yaml);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error parsing YAML: {ex.Message}");
    return 1;
}

if (baseUrlArg != null)
    script.BaseUrl = baseUrlArg;

var runner = new TestRunner(script.BaseUrl);
var allPassed = await runner.RunAsync(script);
return allPassed ? 0 : 1;
