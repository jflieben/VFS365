using Vfs365.Agent;

if (args.FirstOrDefault()?.Equals("mount", StringComparison.OrdinalIgnoreCase) == true)
{
    DetailsWindows.PrepareProcess(); // before any window (the tray with --tray, the details window)
}

return await AgentCommands.RunAsync(args, Console.Out, Console.Error);
