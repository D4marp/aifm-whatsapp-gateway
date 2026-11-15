using WhatsAppDotnet;            // NOTE: the README of the package says WhatsApp.Client, but the real namespace is WhatsAppDotnet
using WhatsAppDotnet.Utilities;

// STEP 1 of MAA-2102: prove that a .NET program can send one message to a WhatsApp group the number has already joined.
// Library: WhatsApp.Client 1.0.7 (browser automation with Playwright, a C# port of whatsapp-web.js).
//
//   dotnet run --project poc\Step1.Console -- link                       show the QR, scan it with the phone, then stop
//   dotnet run --project poc\Step1.Console -- groups                     list the groups of the linked number
//   dotnet run --project poc\Step1.Console -- send "IT Batam" "text"     only PRINTS what it would send (dry run)
//   dotnet run --project poc\Step1.Console -- send "IT Batam" "text" --yes   really sends ONE message
//   dotnet run --project poc\Step1.Console -- listen                     print incoming group messages for 60 seconds
//
// Use a number you can afford to lose. Unofficial clients are against WhatsApp's terms and the number can be blocked.

var mode = args.FirstOrDefault()?.ToLowerInvariant() ?? "help";
if (mode is "help" or "-h" or "--help")
{
    Console.WriteLine("Modes: link | groups | send \"<group name>\" \"<text>\" [--yes] | listen");
    Console.WriteLine("Read the warning in the README before using a real number.");
    return 0;
}

var yes = args.Contains("--yes");
var sessionName = "lab_session";                       // the login is kept under this name, so the QR is scanned only once
// The session folder is the key to the WhatsApp account. It lives OUTSIDE the project folder on purpose, so it is never committed or zipped by accident.
var sessionPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "aifm-whatsapp-lab", "session");
Directory.CreateDirectory(sessionPath);
var options = new WhatsAppClientOptions
{
    SessionName = sessionName,
    SessionPath = sessionPath,
    QrMaxRetries = 5,
    // The browser is visible only when linking. Channel "msedge" uses the Edge already installed: the downloaded Chromium 120 would not start on this PC (side-by-side error).
    PlaywrightOptions = { Headless = mode != "link", Channel = "msedge" }
};

using var client = new WhatsAppClient(options);
var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

client.QrReceived += (_, e) => { Console.WriteLine("Scan this QR with WhatsApp > Linked devices > Link a device:"); Console.WriteLine(e.QrCode); };
client.Authenticated += (_, _) => Console.WriteLine("Authenticated.");
client.AuthenticationFailed += (_, e) => { Console.WriteLine("Authentication failed."); ready.TrySetResult(false); };
client.Ready += (_, _) => { Console.WriteLine("Client is ready."); ready.TrySetResult(true); };
client.Disconnected += (_, _) => Console.WriteLine("Disconnected.");

Console.WriteLine($"Starting ({mode}). This opens a browser session to WhatsApp Web.");
await client.InitializeAsync();
await client.AuthenticateAsync();

var linked = await Task.WhenAny(ready.Task, Task.Delay(TimeSpan.FromMinutes(3))) == ready.Task && ready.Task.Result;
if (!linked) { Console.WriteLine("Not ready within 3 minutes. Run 'link' first and scan the QR."); return 1; }

switch (mode)
{
    case "link":
        Console.WriteLine("Linked. The session is saved; you can close this now.");
        return 0;

    case "groups":
    {
        var chats = await client.GetChatsAsync();
        var groups = chats.Where(c => c.IsGroup).ToList();
        Console.WriteLine($"{groups.Count} group(s):");
        foreach (var g in groups)
        {
            var members = "";
            // --members also asks WhatsApp for the member list of each group (ticket item 2: a group with many members). It is extra activity, so it is optional.
            if (args.Contains("--members") && g is WhatsAppDotnet.Factories.GroupChat gc)
                members = $"  members: {(await gc.GetParticipantsAsync()).Count()}";
            Console.WriteLine($"  {g.Name}  [{g.Id}]{members}");
        }
        return 0;
    }

    case "send":
    {
        var name = args.ElementAtOrDefault(1);
        var text = args.ElementAtOrDefault(2);
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(text) || text.StartsWith("--")) { Console.WriteLine("Usage: send \"<group name>\" \"<text>\" [--yes]"); return 1; }
        if (text.Length > 500) { Console.WriteLine("Text is longer than 500 characters; refused for the lab."); return 1; }

        var chats = await client.GetChatsAsync();
        var group = chats.FirstOrDefault(c => c.IsGroup && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        if (group == null) { Console.WriteLine($"No group named '{name}'. Run 'groups' to see the names."); return 1; }

        Console.WriteLine($"Group: {group.Name} [{group.Id}]");
        Console.WriteLine($"Text:  {text}");
        if (!yes) { Console.WriteLine("Dry run: nothing was sent. Add --yes to send this one message."); return 0; }

        await client.SendMessageAsync(group.Id.ToString(), text);
        Console.WriteLine("Sent one message.");
        return 0;
    }

    case "listen":
    {
        client.MessageReceived += (_, e) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {e.Message.From}: {e.Message.Body}");
        Console.WriteLine("Listening for 60 seconds. Write something in a group from another phone.");
        await Task.Delay(TimeSpan.FromSeconds(60));
        return 0;
    }

    default:
        Console.WriteLine("Unknown mode. Use: link | groups | send | listen");
        return 1;
}
