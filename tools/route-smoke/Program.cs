// Exercises RouteStore (the bridge's port of pin.py) against a throwaway
// status dir. Exit 0 = every rule held.
using System.Text.Json.Nodes;
using ClaudeStatusBridge;

var dir = Path.Combine(Path.GetTempPath(), "route-smoke-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(dir);
var o = new BridgeOptions { MirrorDir = dir, SplitPanels = new[] { 2 } };
var broker = new BrokerClient(o);
var routes = new RouteStore(broker, o);
int failures = 0;
void Check(bool cond, string what) { Console.WriteLine($"  {(cond ? "ok  " : "FAIL")} {what}"); if (!cond) failures++; }
void Live(string sid) { var d = Path.Combine(dir, "sessions", sid); Directory.CreateDirectory(d); File.WriteAllText(Path.Combine(d, "broker.json"), $"{{\"pid\":1,\"port\":1,\"session_id\":\"{sid}\"}}"); }
void Dead(string sid) { var f = Path.Combine(dir, "sessions", sid, "broker.json"); if (File.Exists(f)) File.Delete(f); }
Dictionary<string, string> R() => routes.ReadRoutes();

Console.WriteLine("layout: 2 panels, panel 2 split");
Check(routes.SetPanelLayout(2, 1), "SetPanelLayout(2,1) accepted");
Check(!routes.SetPanelLayout(9, 1), "SetPanelLayout(9,1) rejected");
Check(routes.ReadPanelLayout()?.PanelCount == 2, "ReadPanelLayout sees 2 panels");
Check(string.Join(",", routes.OfferedSlots()) == "1,2a,2b", $"offered slots = {string.Join(",", routes.OfferedSlots())}");

Console.WriteLine("binding");
Live("A"); Live("B"); Live("C");
Check(!routes.Bind("A", "x1"), "invalid slot syntax rejected");
Check(routes.Bind("A", "1") && R()["A"] == "1", "A -> 1");
Check(routes.Bind("B", "1") && R()["B"] == "1" && !R().ContainsKey("A"), "B -> 1 evicts A (one session per slot)");
Check(routes.Bind("B", "2a") && R()["B"] == "2a" && R().Count == 1, "B -> 2a releases 1 (one slot per session)");
Check(routes.FirstFreeSlot() == "1", $"first free slot = {routes.FirstFreeSlot()} (1)");
Check(routes.Bind("A", "1"), "A -> 1");
Check(routes.FirstFreeSlot() == "2b", $"first free slot = {routes.FirstFreeSlot()} (2b)");
Check(routes.Bind("C", "2b") && routes.FirstFreeSlot() is null, "C -> 2b; no free slot left");

Console.WriteLine("pruning");
Dead("B");
Check(routes.Bind("C", "2b") && !R().ContainsKey("B"), "re-binding C prunes dead B");
Check(routes.FirstFreeSlot() == "2a", $"first free slot after prune = {routes.FirstFreeSlot()} (2a)");

Console.WriteLine("unbind");
routes.Unbind("A");
Check(!R().ContainsKey("A") && R()["C"] == "2b", "Unbind A leaves C");
Check(File.Exists(Path.Combine(dir, "routes.json")), "routes.json written to the status dir");

Console.WriteLine("full-vs-half exclusivity");
o.SplitPanels = Array.Empty<int>();
Check(string.Join(",", routes.OfferedSlots()) == "1,2", "offered without split = 1,2");
Check(routes.FirstFreeSlot() == "1", $"first free = {routes.FirstFreeSlot()}: C holds 2b so panel 2 is taken");

Directory.Delete(dir, recursive: true);
Console.WriteLine(failures == 0 ? "PASS" : $"FAIL ({failures})");
return failures == 0 ? 0 : 1;
