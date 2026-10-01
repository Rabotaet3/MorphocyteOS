var configPath = args[Array.IndexOf(args, "-f") + 1];
var config = File.ReadAllText(configPath);
if (args.Contains("-t"))
{
    if (config.Contains("slow-validation")) await Task.Delay(2000);
    if (config.Contains("INVALID")) { Console.Error.WriteLine("Simulated validation failure"); Environment.Exit(2); }
    Console.WriteLine("Validation passed");
    return;
}
if (config.Contains("startup-fail")) Environment.Exit(3);
Console.WriteLine("Fake core started, no networking.");
await Task.Delay(Timeout.Infinite);
