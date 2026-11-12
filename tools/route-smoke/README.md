# route-smoke — check the bridge's routing rules

Compiles `RouteStore` (the bridge's port of the plugin's `pin.py` rules) against
a throwaway status dir and asserts: one slot per session, one session per
slot, dead-session pruning at bind time, split-panel slot offering,
full-vs-half exclusivity, newest-wins layout reads, and layout validation.

```sh
dotnet run --project tools/route-smoke
```

Last run 2026-11-12: PASS (18 checks).
