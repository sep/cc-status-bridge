// Smoke test for SshTunnelLink: spawn headless ssh -L to a local sshd,
// connect, send a ping through the forwarded port to a device stand-in
// (fwtest), expect a pong back on the same link. Exit 0 on success.
using System.Text.Json.Nodes;
using ClaudeStatusBridge;

var o = new BridgeOptions
{
    Transport = "ssh",
    Ssh = new SshOptions
    {
        Host = "127.0.0.1", Port = int.Parse(args[0]), User = args[1],
        ListenPort = int.Parse(args[2]), LocalPort = int.Parse(args[3]),
        ExtraArgs = $"-i {args[4]} -o UserKnownHostsFile=/dev/null -o IdentitiesOnly=yes",
    },
};
using var link = new SshTunnelLink(o);
Console.WriteLine($"link: {link.Describe}");

var gotPong = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
link.LineReceived += doc =>
{
    Console.WriteLine($"  <- {doc.ToJsonString()}");
    if (doc["type"]?.GetValue<string>() == "pong") gotPong.TrySetResult(doc);
};

var deadline = DateTime.UtcNow.AddSeconds(20);
while (!link.IsOpen && DateTime.UtcNow < deadline)
{
    var avail = link.IsDeviceAvailable();
    if (avail && link.TryOpen()) break;
    await Task.Delay(500);
}
if (!link.IsOpen) { Console.WriteLine("FAIL: link never opened"); return 1; }
Console.WriteLine("link open");

for (int seq = 1; seq <= 3 && !gotPong.Task.IsCompleted; seq++)
{
    var ok = link.WriteLine($"{{\"type\":\"ping\",\"seq\":{seq}}}");
    Console.WriteLine($"  -> ping {seq} written={ok}");
    await Task.WhenAny(gotPong.Task, Task.Delay(2000));
}
if (!gotPong.Task.IsCompleted) { Console.WriteLine("FAIL: no pong"); return 2; }
Console.WriteLine("PASS: pong received through the SSH link");
return 0;
