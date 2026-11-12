# ssh-link-smoke — exercise SshTunnelLink without the tray

Compiles the bridge's transport sources (`IDeviceLink`, `SshTunnelLink`,
`BridgeOptions`) into a console app that does what the tray does: spawn a
headless `ssh -L`, connect, send a ping through the forwarded port, expect
a pong on the same link. Pair it with the firmware repo's
`tools/ssh-host-test/fwtest` standing in for the device (or a real device).

```sh
# device stand-in (firmware repo), owning the remote forward:
./fwtest <host> <port> <user> device_key.pub device_key 7777 60
# this harness, as the bridge:
dotnet run --project tools/ssh-link-smoke -- <port> <user> 7777 7778 /path/to/bridge_key
```

Expected tail: `link open`, `-> ping 1 written=True`, a `pong`, `PASS`.
Exit 1 = ssh never produced a usable forward (see the `[ssh]` log lines);
exit 2 = forward opened but nothing answered (device not listening on
that port?). Run 2026-11-12 against OpenSSH 9.6 + fwtest: PASS.
