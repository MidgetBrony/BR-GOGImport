using GOGTestFramework;

Console.Title = "BOXROOM GOG Importer - Experimental";
Console.ForegroundColor = ConsoleColor.Magenta;
Console.WriteLine("EXPERIMENTAL GOG IMPORTER");
Console.ResetColor();
Console.WriteLine("This tool may break, miss games, or change how imported games are stored.");
Console.WriteLine();
Console.WriteLine("If GOG asks you to sign in, copy the FULL redirect URL from your browser's address bar");
Console.WriteLine("and paste that complete URL back into this window.");
Console.WriteLine();
Console.Write("Press Enter to continue...");
Console.ReadLine();

var gog = new GogClient();

await gog.InitializeAsync();
await gog.GetOwnedGamesAsync();
await gog.ImportOwnedGamesAsync();

Console.WriteLine("Press any key to close.");
Console.ReadKey();
