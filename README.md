# BIG.Unity.Networking

Co-op networking toolkit of the BIG ecosystem, built on [FishNet](https://github.com/FirstGearGames/FishNet)
and [BIG.Unity](https://github.com/Big-Ice-Games/BIG.Unity).

FishNet does the replication — your gameplay code uses `NetworkBehaviour`, `SyncVar` and RPCs directly.
This package abstracts what changes between platforms: **identity, rooms (lobbies) and the transport**.
Game code never talks to Steam/EOS/LAN — it talks to `NetworkSession`.

| Provider | Status | Transport |
|---|---|---|
| LAN | ready | Tugboat + UDP broadcast discovery |
| Steam | planned | FishySteamworks + Steam lobbies |
| EOS | planned | FishyEOS + EOS lobbies (cross-play) |

## Installation

Package Manager > `+` > `Install package from git URL`, in this order:

```
https://github.com/Big-Ice-Games/BIG.Unity.git
```
```
https://github.com/FirstGearGames/FishNet.git?path=Assets/FishNet
```
```
https://github.com/Big-Ice-Games/BIG.Unity.Networking.git
```

FishNet must be installed as a package (not imported from the Asset Store into `Assets/`) — until it is,
this package does not compile and the console tells you what is missing.

## Setup

1. **NetworkManager object** (persistent, in your first scene):
   * FishNet `NetworkManager` + `Tugboat` (for LAN). Several providers in one build → put the transports in `Multipass`.
   * `NetworkSession` — assign the NetworkManager and a **provider asset**
     (create with *Create > BIG > Networking > LAN Provider*). Optional *Editor Provider* overrides it in play mode.
   * Set FishNet's scene handling as you like (global scenes recommended for co-op).
2. **Player prefab**: `NetworkObject` + `NetworkTransform` (client authoritative, the default) + `NetworkPlayer`
   + your own movement script that runs only `if (IsOwner)`. Register the prefab in FishNet's spawnable prefabs.
3. **Networked scene** (hub, mission): add `PlayerSpawner` with the player prefab and spawn points.
4. **Testing**: add `NetworkSessionDebugKeys` — **F5** host, **F6** search + join the first room, **F7** leave
   (also in the component's context menu). Run two instances (build + editor, or Multiplayer Play Mode).
   Windows asks for firewall access on first host — allow private networks.

## Session flow

```csharp
var session = NetworkSession.Instance;

session.Host(new SessionSettings { Name = "Willow Creek", MaxPlayers = 4 });

session.Search(rooms => session.Join(rooms[0]));
session.Join(new SessionAddress("LAN", "192.168.1.20:7770"));   // typed address

session.Leave();
```

```
Offline ─Host()──► CreatingRoom ─► StartingHost ─► Hosting
   └────Join()──► JoiningRoom ──► Connecting ───► Connected
any ─Leave() / failure / host lost──► Offline (+ DisconnectReason)
```

* One owner of the flow — never call FishNet's `StartConnection`/`StopConnection` yourself.
* Every attempt has a token: late callbacks of an old attempt are ignored (no "left the lobby right after joining").
* Listen to the `SessionStateChanged` BIG event (or `NetworkSession.StateChanged`); on `Offline` read
  `Reason` (`HostLeft`, `Timeout`, `VersionMismatch`...) and `NetworkSession.LastError` for UI.
* Version check: `NetworkVersion.Current` (game version + plugin protocol) is compared when joining a room
  and again in the hello handshake. Override `NetworkVersion.GameVersion` to decouple from `Application.version`.
* LAN test builds can join on start with the launch argument `-big-join 192.168.1.20:7770`.

## Players

```csharp
public class Scoreboard : BaseBehaviour
{
    [Subscribe] private void OnJoined(NetworkPlayerJoined e) => Add(e.Player.Id, e.Player.DisplayName);
    [Subscribe] private void OnLeft(NetworkPlayerLeft e) => Remove(e.PlayerId);
}

NetworkSession.Instance.Players.Local;              // this machine's NetworkPlayer
NetworkSession.Instance.Players.TryGet(id, out p);  // by stable PlayerId
```

* **`PlayerId`** is the stable identity (SteamID, EOS id, LAN guid) — key everything that belongs to a player by it,
  never by FishNet connection id (that changes on reconnect / host migration).
* After FishNet authentication every client sends a hello (`PlayerId`, name, version). The server takes the identity
  from the transport when it can vouch for it (Steam) and from the claim otherwise (LAN).
* `PlayerSpawner` spawns once per `PlayerId` when the player is identified AND has loaded the start scenes;
  a reconnecting player gets their object back. The owner always comes from the server's connection.

## Writing a provider

Implement `INetworkProvider` (platform + `ISessionService` + transport binding) and a `NetworkProviderAsset`
that creates it. FishNet initializes its transport once, so the provider does not add transports — it finds its
own on the NetworkManager (`TransportManager.GetTransport<T>()`, Multipass-aware) and configures it in
`PrepareHost` / `PrepareClient`. See `Lan/` for the reference implementation.

## License

MIT — see [LICENSE.md](LICENSE.md). Required third-party packages: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
