using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ClaudeStatusBridge;

/// <summary>
/// The bridge as a WRITER of routes.json / panel_layout.json (MANAGEMENT.md
/// §2). A faithful port of the plugin's pin.py rules, which the WSL/Windows
/// convergence work depends on:
///
///  - one slot per session, one session per slot: binding S→slot evicts any
///    other session on slot and releases any other slot S held;
///  - prune at bind time: routes whose session has no broker.json in any
///    base are dropped on every write;
///  - write to every target: the well-known status dir plus every data dir
///    we found a routes.json in, so plugin-side readers stay coherent;
///  - panel layout: read the NEWEST copy, write all targets.
///
/// Reads go through BrokerClient.CandidateDataRoots() (the same bases the
/// subscription manager uses).
/// </summary>
public sealed class RouteStore
{
    private static readonly Regex SlotPattern = new(@"^[1-9][0-9]*[ab]?$", RegexOptions.Compiled);

    private readonly BrokerClient  _broker;
    private readonly BridgeOptions _options;

    public RouteStore(BrokerClient broker, BridgeOptions options)
    {
        _broker  = broker;
        _options = options;
    }

    public static bool IsValidSlot(string slot) => !string.IsNullOrEmpty(slot) && SlotPattern.IsMatch(slot);

    public Dictionary<string, string> ReadRoutes() => _broker.ReadAllRoutes();

    /// <summary>Bind with two-axis displacement + pruning. False only on invalid slot syntax.</summary>
    public bool Bind(string sessionId, string slot)
    {
        if (!IsValidSlot(slot)) return false;
        var routes = ReadRoutes();
        var toRemove = routes
            .Where(kv => (kv.Value == slot && kv.Key != sessionId)
                      || (kv.Key == sessionId && kv.Value != slot)
                      || (kv.Key != sessionId && !SessionHasBrokerState(kv.Key)))
            .Select(kv => kv.Key)
            .ToList();
        foreach (var sid in toRemove) routes.Remove(sid);
        routes[sessionId] = slot;
        WriteRoutes(routes);
        Log.Info($"[routes] bound {Shorten(sessionId)} -> {slot}" +
                 (toRemove.Count > 0 ? $" (displaced/pruned {toRemove.Count})" : ""));
        return true;
    }

    /// <summary>Remove this session's binding from every routes.json. Idempotent.</summary>
    public void Unbind(string sessionId)
    {
        foreach (var root in _broker.CandidateDataRoots())
        {
            var path = Path.Combine(root, "routes.json");
            if (!File.Exists(path)) continue;
            try
            {
                if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject obj) continue;
                if (!obj.ContainsKey(sessionId)) continue;
                obj.Remove(sessionId);
                AtomicWrite(path, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { Log.Warn($"[routes] failed to update {path}: {ex.Message}"); }
        }
        Log.Info($"[routes] unbound {Shorten(sessionId)}");
    }

    /// <summary>Every slot the current layout offers, honouring the split list: "1", "2a", "2b", …</summary>
    public List<string> OfferedSlots()
    {
        var layout = ReadPanelLayout();
        var count  = layout?.PanelCount ?? 1;
        var first  = layout?.FirstId ?? 1;
        var split  = new HashSet<int>(_options.SplitPanels ?? Array.Empty<int>());
        var slots  = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var n = first + i;
            if (split.Contains(n)) { slots.Add($"{n}a"); slots.Add($"{n}b"); }
            else slots.Add(n.ToString());
        }
        return slots;
    }

    /// <summary>Lowest offered slot no live session holds, or null.</summary>
    public string? FirstFreeSlot()
    {
        var routes = ReadRoutes();
        var taken = new HashSet<string>(
            routes.Where(kv => SessionHasBrokerState(kv.Key)).Select(kv => kv.Value), StringComparer.Ordinal);
        // A full panel is taken if either half is, and vice versa.
        foreach (var s in OfferedSlots())
        {
            var panel = s.TrimEnd('a', 'b');
            if (taken.Contains(s) || taken.Contains(panel) || taken.Contains(panel + "a") && s == panel || taken.Contains(panel + "b") && s == panel)
                continue;
            return s;
        }
        return null;
    }

    // ---------------- panel layout ----------------

    public BrokerClient.PanelLayout? ReadPanelLayout()
    {
        BrokerClient.PanelLayout? newest = null;
        DateTime newestAt = DateTime.MinValue;
        foreach (var root in _broker.CandidateDataRoots())
        {
            var path = Path.Combine(root, "panel_layout.json");
            if (!File.Exists(path)) continue;
            try
            {
                var mtime = File.GetLastWriteTimeUtc(path);
                if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject n) continue;
                if (mtime <= newestAt) continue;
                newestAt = mtime;
                newest = new BrokerClient.PanelLayout(
                    n["panel_count"]?.GetValue<int>() ?? 1,
                    n["panel_width"]?.GetValue<int>() ?? 64,
                    n["panel_height"]?.GetValue<int>() ?? 32,
                    n["layout"]?.GetValue<string>() ?? "horizontal",
                    n["first_id"]?.GetValue<int>() ?? 1);
            }
            catch { }
        }
        return newest;
    }

    /// <summary>Validate like pin.set_panel_layout and write to every target. False on invalid values.</summary>
    public bool SetPanelLayout(int panelCount, int firstId, int panelWidth = 64, int panelHeight = 32, string layout = "horizontal")
    {
        if (panelCount is < 1 or > 4) return false;
        if (panelWidth is not (32 or 64)) return false;
        if (panelHeight is not (16 or 32 or 64)) return false;
        if (layout is not ("horizontal" or "vertical" or "serpentine")) return false;
        if (firstId is < 1 or > 99) return false;
        var obj = new JsonObject
        {
            ["panel_count"]  = panelCount,
            ["panel_width"]  = panelWidth,
            ["panel_height"] = panelHeight,
            ["layout"]       = layout,
            ["first_id"]     = firstId,
        };
        var json = obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        foreach (var root in WriteTargets())
        {
            try { AtomicWrite(Path.Combine(root, "panel_layout.json"), json); }
            catch (Exception ex) { Log.Warn($"[panel] failed to write in {root}: {ex.Message}"); }
        }
        Log.Info($"[panel] layout set: {panelCount} panel(s) from id {firstId}, {panelWidth}x{panelHeight} {layout}");
        return true;
    }

    // ---------------- internals ----------------

    private bool SessionHasBrokerState(string sessionId) =>
        _broker.CandidateDataRoots().Any(root => File.Exists(Path.Combine(root, "sessions", sessionId, "broker.json")));

    /// <summary>
    /// The well-known status dir always, plus every candidate root that already
    /// holds a routes.json (a plugin data dir the hook writes to). Mirrors
    /// pin.targets() from the bridge's side of the fence.
    /// </summary>
    private IEnumerable<string> WriteTargets()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var wellKnown = _options.ResolvedMirrorDir
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude-status");
        if (seen.Add(wellKnown)) yield return wellKnown;
        foreach (var root in _broker.CandidateDataRoots())
            if (File.Exists(Path.Combine(root, "routes.json")) && seen.Add(root))
                yield return root;
    }

    private void WriteRoutes(Dictionary<string, string> routes)
    {
        var obj = new JsonObject();
        foreach (var kv in routes.OrderBy(kv => kv.Value, StringComparer.Ordinal)) obj[kv.Key] = kv.Value;
        var json = obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        foreach (var root in WriteTargets())
        {
            try { AtomicWrite(Path.Combine(root, "routes.json"), json); }
            catch (Exception ex) { Log.Warn($"[routes] failed to write in {root}: {ex.Message}"); }
        }
    }

    private static void AtomicWrite(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }

    private static string Shorten(string id) => id.Length > 8 ? id[..8] : id;
}
