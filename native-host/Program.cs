using AirDesk.Companion;

if (!BrowserBridge.IsHostInvocation(args))
{
    Console.Error.WriteLine("AirDesk Browser Host: unrecognized extension origin.");
    return 1;
}

await BrowserBridge.RunHost();
return 0;
