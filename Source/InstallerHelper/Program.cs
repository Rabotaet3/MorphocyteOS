using MorphocyteRouter;

// This helper has no WPF/AppSettings/MainWindow/core-process dependencies and never connects VPN.
return args.Length == 3 ? InstallerMaintenance.Run(args[0], args[1], args[2],
    error => Console.Error.WriteLine(error.GetType().Name + ": " + error.Message)) : 1;
