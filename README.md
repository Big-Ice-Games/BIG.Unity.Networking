# BIG.Unity.Networking

Co-op networking toolkit of the BIG ecosystem, built on [FishNet](https://github.com/FirstGearGames/FishNet)
and [BIG.Unity](https://github.com/Big-Ice-Games/BIG.Unity).

FishNet does the replication — your gameplay code uses `NetworkBehaviour`, `SyncVar` and RPCs directly.
This package abstracts what changes between platforms: **identity, rooms (lobbies) and the transport**.
Game code never talks to Steam/EOS/LAN — it talks to `NetworkSession`.

| Provider | Status | Transport |
|---|---|---|
| LAN | ready | Tugboat + UDP broadcast discovery |
| Steam | ready (needs Steamworks.NET) | FishySteamworks (bundled) + Steam lobbies |
| EOS | planned | FishyEOS + EOS lobbies (cross-play) |

## Installation

Package Manager > `+` > `Install package from git URL`, in this order:

```
https://github.com/Big-Ice-Games/BIG.Unity.git
```
```
https://github.com/Big-Ice-Games/FishNet.git?path=Assets/FishNet#big
```
```
https://github.com/Big-Ice-Games/BIG.Unity.Networking.git
```

FishNet must be installed as a package (not imported from the Asset Store into `Assets/`) — until it is,
this package does not compile and the console tells you what is missing.

**Steam (optional):** also install Steamworks.NET — the Steam provider compiles only when it's present:
```
https://github.com/rlabrecque/Steamworks.NET.git?path=/com.rlabrecque.steamworks.net#2025.165.0
```

FishNet comes from the Big Ice Games fork (branch `big`): upstream FishNet plus a codegen fix that makes it load
on Unity 7 / CoreCLR (overrides of `[MakePublic]` methods were left narrower than their base — Mono tolerated it,
CoreCLR throws `TypeLoadException` on startup). On Unity 6 the upstream URL works as well:
`https://github.com/FirstGearGames/FishNet.git?path=Assets/FishNet`.

## Setup

1. **NetworkManager object** (persistent, in your first scene):
   * FishNet `NetworkManager` + `Tugboat` (for LAN). Several providers in one build → put the transports in `Multipass`.
   * `NetworkSession` — assign the NetworkManager and a **Network Config** (see below).
   * Set FishNet's scene handling as you like (global scenes recommended for co-op).
2. **Player prefab**: `NetworkObject` + `NetworkTransform` (client authoritative, the default) + `NetworkPlayer`
   + your own movement script that runs only `if (IsOwner)`. Register the prefab in FishNet's spawnable prefabs.
3. **Networked scene** (hub, mission): add `PlayerSpawner` with the player prefab and spawn points.
4. **Testing**: add `NetworkSessionDebugKeys` — **F5** host, **F6** search + join the first room, **F7** leave
   (also in the component's context menu). Run two instances (build + editor, or Multiplayer Play Mode).
   Windows asks for firewall access on first host — allow private networks.

## Network Config

**Every setting lives in one asset**: *Create > BIG > Networking > Network Config*, assigned to `NetworkSession`.
Also under **Project Settings > BIG Networking** (pick / edit / create it there).

| Section | What | Applies |
|---|---|---|
| Platform | provider asset for builds (*Create > BIG > Networking > LAN / Steam Provider*), optional provider for the editor | next start |
| Timing | tick rate, physics mode | **live** |
| Connection | connect timeout | next Host / Join |
| Room defaults | name template (`{player}'s game`), max players, visibility, password | next `Host()` |
| Lobby | lobby prefab (empty = no lobby, no host migration) | next Host |
| Host migration | mode, round timeout, state interval | **live** |
| Dev tools | developer overlay (Off / Development only / Always), toggle key, match scene for its "Start match" | **live** |

Edit it in play mode from the inspector: live sections apply at once. Values set from code at runtime
(`session.HostMigration = ...`) override the asset without changing it. Every machine must use the same config
(it ships with the build).

## Session flow

```csharp
var session = NetworkSession.Instance;

session.Host();                                                  // room defaults from the Network Config
session.Host(new SessionSettings { Name = "Willow Creek", MaxPlayers = 4, Password = "aliens" });

session.Search(rooms => session.Join(rooms[0]));
session.Join(room, "aliens");                                    // locked room (room.IsLocked)
session.Join(new SessionAddress("LAN", "192.168.1.20:7770"));   // typed address
session.QuickPlay();                                             // "Play": join any open room, else host a public one

session.SetRoomName("Willow Creek — night 2");                   // host, any time
session.SetRoomPassword("ufo");                                  // host: lock ("" opens it again)
session.Sessions.SetVisibility(SessionVisibility.Public);        // host: private party → open for strangers (and back)
session.Leave();
```

* **Room name**: empty name = the config's template, `{player}` becomes the host's display name.
* **Password**: never published — the room only shows a lock (`SessionInfo.IsLocked`, data key `big.locked`). The
  joining player sends it in the hello handshake, the host checks it; wrong or missing → Offline with
  `DisconnectReason.WrongPassword`. Quick play skips locked rooms. Players inside learn a changed password, so a
  successor host keeps the room locked after a migration. (LAN sends it unencrypted — it's a room lock, not security.)

* **Quick play** searches, joins the fullest joinable room of this version, moves on to the next room when a join
  fails (full, closed, unreachable), and hosts a new public room when nothing is left.
* **Visibility** can change any time: start as a private party with invited friends, open it to the public while
  waiting, or just start the match with who's there.

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

## Developer overlay

`NetworkSession` creates it from the Network Config (*Dev tools*). No scene setup. *Development only* (default) means:
editor, **Development Build**, or any build with the scripting define **`DEVELOPMENT`** — add it in *Project Settings >
Player > Scripting Define Symbols*, or better in a separate test **Build Profile** (*Player Settings Overrides*), so test
builds have the overlay and the release profile doesn't. `DevOverlay.IsDevelopment` tells your own code the same.
The editor creates `Assets/BIG.Networking/DevOverlayPanelSettings.asset` and assigns it to the config (*Dev Overlay Panel*):
UI Toolkit text needs data only an editor-made PanelSettings asset carries — commit that asset.

* **Modules** — Session, Rooms (offline), Lobby, Players (in a session), Voice, Microphone, Audio output, plus whatever
  your game registers. Each one collapses / expands on its header (remembered per module); collapsed modules show a
  short badge (role, phase, player count...) and cost nothing. `+` / `–` expand / collapse all, `◄►` docks left / right.
* **F1** (configurable) hides / shows the whole overlay. `DevOverlay.IsTyping` is true while one of its text fields has
  focus — check it in your input code so typing a room name doesn't walk your character.
* UI Toolkit: the look is in `Runtime/DevTools/Resources/BIG.Networking/` (`DevOverlay.uxml`, `DevModule.uxml`,
  `DevOverlayStyle.uss`) — restyle there, code only fills in data.

**Your own module** — e.g. AI state, quest flags, cheats:

```csharp
public sealed class AliensPanel : DevPanel
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Register() => DevOverlay.Register(new AliensPanel());

    public override string Title => "Aliens";
    public override bool IsAvailable => Session.IsInGame;                 // hidden otherwise
    public override string Badge => $"{AlienDirector.Count} alive";       // shown while collapsed

    public override void Build(DevUI ui)                                  // once; lambdas refresh ~10× a second
    {
        ui.Text(() => $"Wave {AlienDirector.Wave}", () => AlienDirector.Enraged ? DevUI.BAD : null);
        var row = ui.Row();
        row.Button("Spawn one", AlienDirector.SpawnOne, () => Session.IsHost);
        row.Button("Kill all", AlienDirector.KillAll, () => Session.IsHost);
        ui.List(() => AlienDirector.All, alien => alien.Id, (r, alien) => r.Text(() => $"{alien.name}: {alien.State}"));
    }
}
```

`DevUI`: `Text`, `Heading`, `Button`, `Field`, `Bar` (0..1 level), `Row`, `Section(visible)`, `List(items, key, row)`,
`Add` for any UI Toolkit element, `Bind` for your own refresh logic. Text styles: `DevUI.GOOD / WARN / BAD / MUTED`.

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

## Lobby and matches

Joining a room connects you to the host right away, but nothing is spawned yet — players gather in the **lobby**,
the host starts the match when they want (alone, with the party, or after strangers joined).

Setup: a prefab with `NetworkObject` (**Is Global** on) + `NetworkLobby`, assigned to the Network Config → *Lobby Prefab*
(FishNet must know the prefab — its DefaultPrefabObjects picks it up). The host spawns it when hosting starts.
Put the `PlayerSpawner` in the **match** scene, not the menu/lobby scene.

**Required for networked objects placed in the match scene:** add FishNet's `ObserverManager` to the NetworkManager with
`SceneCondition` (FishNet package: `Runtime/Observing/Conditions/ScriptableObjects/SceneCondition.asset`) in *Default
Conditions*. Without it the host sends the scene's objects as soon as IT loaded the scene; clients that haven't loaded it
yet log `SceneId … not found` and never see them (the host does, so it's easy to miss).

```csharp
var lobby = NetworkLobby.Instance;

lobby.Members;                         // PlayerId, DisplayName, IsHost, Ready, Data — on every machine
lobby.SetReady(true);                  // any player
lobby.SetMemberData("character:sheriff");

// Host
lobby.SetSetting("map", "WillowCreek");
if (lobby.AllReady) lobby.StartMatch("Mission_WillowCreek");   // seed: random unless given
lobby.ReturnToLobby();
lobby.Kick(playerId);                  // the kicked player ends Offline with DisconnectReason.Kicked

[Subscribe] private void OnLobby(LobbyChanged e) { /* refresh lobby UI */ }
```

* **Phase** `Lobby → Loading → InGame`. The match scene loads **additively** over the scene the session started in
  and becomes the active scene; returning unloads it. Players joining during the match load it too.
* **Seed + Settings** are for scenes that aren't fully baked: every machine generates the same layout locally
  from `lobby.Seed` (see `TestSeededLayout` in the dev project) — no need to network the layout.
* **Match-scene scripts must wait for the lobby**: `NetworkLobby.WhenAvailable(lobby => Build(lobby.Seed))`.
  A player joining mid-match loads the match scene *before* the lobby object (and its seed) arrives —
  reading `NetworkLobby.Instance` in `Start` fails for them.

**Room rules** — a `LobbyRules` asset (*Create > BIG > Networking > Lobby Rules*) on the lobby prefab, one per game
mode if you like; the host can swap them at runtime (`lobby.Rules = ...`, use a runtime copy, not the asset):

| Rule | Values |
|---|---|
| Join In Progress | `Anyone` (drop-in) · `ReturningPlayers` (only who was in this match — crash/disconnect recovery, default) · `Nobody` |
| Ban Kicked Players | kicked players can't come back to this room (default on) |
| Reset Ready After Match | everyone not-ready back in the lobby |

The host checks the rules when a player arrives (`NetworkPlayers.ServerAdmission`); refused players end Offline with
`DisconnectReason.MatchInProgress` / `Banned`. The room publishes whether newcomers can get in
(`SessionInfo.IsOpenForNewPlayers`, `IsMatchInProgress`) — quick play and room lists skip closed matches, while the room
stays joinable at platform level so returning players get back via the list or an invite.
* When the session ends (left, kicked, refused, host lost) `NetworkSession` unloads every scene the network loaded on
  this machine — the player is back in the scene the session started in. This also covers a player refused right after
  connecting, who already received the running match's scene.
* Voice chat needs avatars (`VoiceSpeaker` lives on the player prefab) — there's no voice in the lobby phase yet.

## Host migration

When the host drops, the room carries on with a successor instead of ending for everyone. Set the mode on
the Network Config (*Host Migration*, also settable at runtime: `session.HostMigration`):

| Mode | Host crashes / loses connection | Host leaves on purpose (`Leave()`, quitting) |
|---|---|---|
| Off | session over for everyone | session over for everyone |
| When Host Drops | migration | session over (`DisconnectReason.HostLeft`) — unless the host calls `LeaveAndHandOver()` |
| Always (default) | migration | migration |

Any number of players works — with two, the one who stays becomes the host and plays on alone (others can join it).

How it works (needs the lobby prefab — the lobby knows who succeeds):

1. Every client keeps a copy of the lobby: members in join order, the address each could host on (sent in the hello
   handshake), phase, seed, match scene, settings, bans.
2. The host vanishes without saying goodbye → clients go to `SessionState.Migrating` and unload the match scene.
3. Successors are tried in lobby order (lost host skipped). Whose turn it is starts hosting the same room
   (`ISessionService.SetHost`: LAN starts its own beacon, Steam hands the lobby ownership over); the others connect to
   its address, retrying. A successor that isn't up within `Migration Round Timeout` (12 s) is skipped. Every machine
   is in its own list, so in the worst case the last one standing hosts alone.
4. The new host's `NetworkLobby` restores the room: Ready/Data come back as members reconnect, settings and bans stay,
   and a running match reloads **from the same scene and seed** — everyone is let back in, whatever `JoinInProgress` says.
5. `HostMigrated` (BIG event, `session.Migrated`) fires on every machine that made it. Avatars respawn and the owner puts
   its own back at its last pose (`NetworkPlayer` → *Keep Pose On Host Migration*).

Restore what the local player owns (health, inventory) in `HostMigrated`. Typical hitch: 1–3 s after the transport
notices the loss — immediately on hand-over; a crash waits for the connection timeout (LAN provider asset:
*Connection Timeout*, 6 s; Steam: about 10 s).

### World state (AI, items, breakables)

Everything the host controls survives when it has **`NetworkMigratable`** on its root:

* **Scene objects** (placed in the scene, e.g. windows, doors, crates) come back with their pose and state; ones the host
  had despawned (picked up, destroyed) are despawned again. They're matched by their place in the scene hierarchy.
* **Spawned objects** (AI, dropped items) are spawned again from the same prefab (default spawnable collection), with
  pose, state and owner. An object a player owned is handed back when they reconnect.
* **State**: implement `IMigratable` on any component of the object (or its children):

```csharp
public sealed class Alien : NetworkBehaviour, IMigratable
{
    private readonly SyncVar<int> _health = new SyncVar<int>(100);
    private Vector3 _target;          // host-only brain state — fine, it's written on the host

    public void WriteMigrationState(Writer writer)   // host, every interval while it changes
    {
        writer.WriteInt32(_health.Value);
        writer.WriteVector3(_target);
    }

    public void ReadMigrationState(Reader reader)    // new host, right after the object is back
    {
        _health.Value = reader.ReadInt32();
        _target = reader.ReadVector3();
    }
}
```

The host writes the state every *Migration State Interval* (Network Config, 1 s) and sends it to the clients only
when the bytes changed (buffered, so late joiners have it too). So up to one interval of host-only changes can be lost;
poses are taken from what the client saw at the moment of the loss.

**Spawners must not double up.** A host-side spawner that creates the initial AI/items checks first:

```csharp
public override void OnStartServer()
{
    if (NetworkSession.Instance.IsRestoringAfterMigration(gameObject.scene)) return; // they come back from the migration
    SpawnInitialAliens();
}
```

Not covered: player avatars (`PlayerSpawner` respawns them — don't add `NetworkMigratable` there), nested NetworkObjects,
Rigidbody velocities (comes with the physics profile). Objects without the component: scene objects come back as baked /
generated, spawned objects don't come back.

Known limits: a client whose OWN connection breaks also thinks the host dropped — if it's first in line it ends up
hosting alone while the others stay with the old host. A host that only lost its network keeps hosting by itself.

Test: *Leave & hand over* on the host in the test HUD, or kill the host's process (Task Manager) mid-match. The test
match has windows, crates and a battery (E next to them: break / remove / use) and two drones the host flies around —
all of it should look the same after the migration.

## Making networked objects

**Make Networked** — right click an object in the Hierarchy (or a prefab in the Project window) > *BIG Networking >
Make Networked*:

| Preset | Adds | For |
|---|---|---|
| Player | NetworkObject, NetworkTransform, NetworkBody (Player profile), NetworkPlayer, NetworkInteractor, collider | the avatar |
| Host-controlled | NetworkObject, NetworkTransform, NetworkBody (Host profile), NetworkMigratable | AI, moving platforms |
| Pushable prop | physics base* + NetworkPushable | heavy crates, barrels |
| Carryable item | physics base* + Pushable + NetworkCarryable (+ NetworkItem) | boxes, tools — hand, throw, use |
| Inventory item | physics base* + Pushable + Carryable + NetworkCollectible | flashlight, keycard — inventory ⇄ hand |
| Interactable | NetworkObject, NetworkInteractable, NetworkMigratable, collider | switches, doors, terminals |
| Scene state only | NetworkObject, NetworkMigratable | state without interaction (destructible wall) |

\* physics base: Rigidbody, collider (mesh colliders made convex), NetworkTransform, NetworkBody (Physics-prop profile),
NetworkMigratable.

Scene objects get their FishNet SceneId, prefabs go on FishNet's spawnable list. Profiles are shared assets in
`Assets/BIG.Networking/Profiles/` (edit them to tune every object of that kind). Undo works.

**Inspector check** — `NetworkBody` and `NetworkMigratable` show a one-line summary (who moves it · who simulates ·
survives migration? · what players can do · scene object / spawnable), every setup problem with its fix button (named by
what it does: *Add Rigidbody*, *Make convex*...) — no profile, physics
without Rigidbody, touch without (convex) collider, prefab not spawnable, scene object without SceneId, host-controlled
object that wouldn't survive a migration, avatar with NetworkMigratable, taken object with a Host-authority profile — and
buttons to add behaviours (see below). One card per object (on NetworkBody, or NetworkMigratable when there's no body).

**`NetworkBaseBehaviour`** — `BaseBehaviour` for networked scripts:

```csharp
public sealed class Alien : NetworkBaseBehaviour
{
    [Inject] private AlienDirector _director;                       // resolved in Awake, like BaseBehaviour

    [Migrate] private readonly SyncVar<int> _health = new SyncVar<int>(100);  // survives host migration
    [Migrate] private Vector3 _target;                              // host-only brain state — survives too

    protected override void OnNetworkReady(NetworkRole role)        // once live on this machine
    {
        if (role.IsHostSide && !WasMigrated) _target = _director.PickTarget();  // fresh aliens only
    }

    [Subscribe] private void OnNoise(NoiseMade noise) { ... }        // BIG events while on the network
}
```

* `[Inject]` in Awake, `[Subscribe]` handlers active from network start to stop, `Log`, `RaiseEvent`.
* `OnNetworkReady(role)` — one entry point instead of OnStartServer / OnStartClient: `role.IsHostSide`, `role.IsOwner`.
  `OnOwnershipChanged(role)` when it's picked up / returned. Overriding FishNet callbacks: call base.
* `[Migrate]` on plain fields or `SyncVar<T>`: bool, int, uint, long, float, double, string, enums, Vector2/3/4,
  Quaternion, Color, Color32 (others: override `OnWriteMigration` / `OnReadMigration`). The object needs
  `NetworkMigratable`. `WasMigrated` = this object came back from a migration (skip first-time setup).

## What players can do with objects

`NetworkBody` only says how an object moves and who simulates it. What players DO with it are separate behaviours —
add them with the buttons on the NetworkBody card (*+ Push, + Carry, + Collect, + Interact*) or a Make Networked preset:

| Behaviour | Players can | Notes |
|---|---|---|
| `NetworkPushable` | push it by walking into it | the pusher owns + simulates it (no lag); back to the host when it rests |
| `NetworkCarryable` | pick up into the hand, **use**, throw, drop | use → `IItemUse.OnItemUsed(user, isHostSide)` on the user's machine and the host |
| `NetworkCollectible` | collect into the inventory, take out into the hand, put away, drop from the inventory | inventory UI / slots / limits are the game's |
| `NetworkInteractable` | use it in place (switch, door, window) | `IInteractionHandler.OnInteract(player)` runs on the host; `Prompt` for the UI |

Carryable and Collectible sit on **`NetworkItem`** (added with them): the item's state — `World` / `Held` / `Stowed` — and
holder, synced and applied on every machine (visible / hidden, colliders, follows the holder's hand —
`IItemAnchors` on your avatar gives the hand; without it, in front of the avatar). The item object stays alive in an
inventory, so it keeps its own state (charge, ammo, on / off) and survives host migration. Its holder disconnecting
drops it where they stood.

```csharp
NetworkItem.ItemsOf(player);            // a player's hand + inventory — build the inventory UI from this
NetworkItem.HeldBy(player);
NetworkItem.AnyChanged += RefreshInventoryUi;
NetworkItem.ServerTakeRule = (player, item, into) => Inventory.HasRoom(player, item);   // host: inventory limits
```

**`NetworkInteractor`** on the avatar finds what the local player can act on (in range, in view — `Max Angle` 180 for
top-down) and does it. It reads no input — bind your keys:

```csharp
interactor.FocusPrompt;      // "Pick up Box", "Break window" — for the prompt UI (FocusAltPrompt: "Collect Flashlight")
interactor.Primary();        // E: interact / pick up (or collect, when it can't be held)
interactor.Collect();        // into the inventory
interactor.Use(); interactor.Throw(); interactor.Drop(); interactor.PutAway();
interactor.TakeOutNext();    // cycle inventory → hand;  interactor.Held, interactor.Inventory
```

**Holding: grips, poses, hands** — e.g. a broom held with both hands:

* On the **item** (`NetworkItem`, *Holding*): **Grip** — an empty child where the fist closes (position = inside of the
  fist, rotation = the hand's); placed exactly on the holder's hand. **Off Hand Grip** — second point for the other hand
  (IK target). **Hold Pose** — the animation id ("Broom", "TwoHanded"...). Select the item or a grip: the Scene view shows
  them (sphere = palm, blue = the hand's forward, green = up). The card's *Create Grip* button adds one.
* On the **avatar**: `IItemAnchors.HandAnchor` — the palm (hand bone or an empty child of it). `IItemHolder` — called on
  every machine when this player's held item changes (also for late joiners / after a migration):

```csharp
public sealed class PlayerHands : MonoBehaviour, IItemAnchors, IItemHolder
{
    [SerializeField] private Transform _rightPalm;
    [SerializeField] private Animator _animator;
    private NetworkItem _held;

    public Transform HandAnchor => _rightPalm;
    public Transform StowAnchor => null;

    public void OnHeldItemChanged(NetworkItem previous, NetworkItem current)
    {
        _held = current;
        _animator.SetInteger("HoldPose", HoldPoses.Id(current?.HoldPose));   // closed fist, arms on the handle
    }

    private void OnAnimatorIK(int layer)
    {
        var offHand = _held != null ? _held.OffHandGrip : null;
        _animator.SetIKPositionWeight(AvatarIKGoal.LeftHand, offHand != null ? 1f : 0f);
        if (offHand != null) _animator.SetIKPosition(AvatarIKGoal.LeftHand, offHand.position);
    }
}
```

  Per frame: the Animator moves the hand → IK → the item (LateUpdate) puts its Grip on the palm — same on every machine,
  no network jitter. The test player (`TestHands`) does this with spheres: the fist closes, the left hand sits on the broom.

A flashlight, for example:

```csharp
public sealed class Flashlight : NetworkBaseBehaviour, IItemUse
{
    [Migrate] private readonly SyncVar<bool> _on = new SyncVar<bool>();
    public void OnItemUsed(PlayerId user, bool isHostSide) { if (isHostSide) _on.Value = !_on.Value; }
}
```

The developer overlay's **Interaction** module shows the focus, the hand and the inventory with buttons.

## Network profiles: authority, sync, physics

Game-wide timing (tick rate, physics mode) is in the **Network Config** (*Timing*, live). Per object type:

**`NetworkBodyProfile`** (*Create > BIG > Networking > Network Body Profile*) per TYPE of object, used by the
**`NetworkBody`** component (next to NetworkObject + NetworkTransform, Rigidbody optional). Presets in the asset's ⋮ menu:

| Preset | Authority | Physics | Ownership return |
|---|---|---|---|
| Player (client authority) | owner moves it | none | never (the avatar stays yours) |
| Host-controlled (AI, doors) | host | host simulates | — |
| Physics prop (owner-simulated) | owner | **owner simulates** | back to the host when it rests |

* **Authority** — `Owner`: the owner moves it, the others follow (no owner = host). `Host`: always the host.
* **Movement sync** — position / rotation / scale, send interval, interpolation, extrapolation (written into the
  NetworkTransform in `Awake`; the profile is the source of truth).
* **Physics** — only the simulating machine has a dynamic Rigidbody, everyone else keeps it kinematic and follows the
  synced transform. `Owner`: whoever owns it simulates (no lag for the one pushing / throwing), no owner = host.
  `Host`: always the host. Recomputed on every ownership change. *Continuous Collision* (on by default) while simulated —
  thin / fast objects don't slip through floors; *Recover Below Y*: fallen through the map → back to its last resting place.
  Level floors should still be thick colliders, not zero-thickness planes.
* **Ownership** — players get an object only through its behaviours (Pushable, Item...: each implements
  `IOwnershipRule`, the host asks them); a body without any can't be taken. *Auto Return To Host*: the owner hands it back
  once it rests (*Return When Resting For*), after *Return After* at the latest; always when the owner disconnects (it
  isn't despawned with them). For your own behaviours: `RequestOwnership`, `ReturnToHost`, `KeepOwnership`, `SetHeld`,
  `Launch`, `ApplySync(paused)`.
* Predicted bodies (FishNet CSP, replicate/reconcile — for host-authority games) are planned, not in yet.

## Voice chat (`BIG.Unity.Networking.Voice`)

Proximity voice and walkie-talkies over the same FishNet session — no extra service. Opus (managed
[Concentus](https://github.com/lostromb/concentus), shipped in `Plugins/`) at 48 kHz mono, 20 ms frames, ~24 kbps per
speaker, unreliable channel, in-band FEC + packet loss concealment, adaptive jitter buffer.

```
owner: MicrophoneInput → 48 kHz → gate (Off / PushToTalk / Open+VAD, or radio button) → Opus → ServerRpc
host:  relays to every other player (never back to the speaker), raises PlayerSpoke (BIG event, AI hook)
others: jitter buffer → decode → VoiceEmitter on the head (3D)
        radio frames also → every VoiceRadio on the frequency (filtered, delayed, squelch)
```

Setup on the player prefab:
1. **VoiceSettings** asset (*Create > BIG > Networking > Voice Settings*) — bitrate, VAD, jitter buffer, distances,
   radio delay/filter/squelch. Tune it live in play mode.
2. **VoiceSpeaker** next to `NetworkPlayer` — assign the settings and the mouth emitter.
3. Child "Mouth" with **VoiceEmitter** (profile *Proximity*). Set the AudioSource *Output* to your voice mixer group
   (e.g. Sonity's `voice`).
4. Walkie-talkie: child/item with **VoiceEmitter** (profile *Radio*) + **VoiceRadio** (frequency, on/off). Its holder
   defaults to the parent player (own transmissions don't come out of the own radio).

Game code:
```csharp
microphone.PushToTalkPressed = input.PushToTalk;     // MicrophoneInput from BIG.Unity (mode Off / PushToTalk / Open)
speaker.RadioTransmitting = input.RadioHeld;         // heard nearby AND through radios on speaker.RadioFrequency
speaker.Muted = true; speaker.Volume = 0.5f;         // per player, persisted by PlayerId

[Subscribe] private void OnSpoke(PlayerSpoke e) { /* server: monsters hear e.Loudness at e.Position */ }
speaker.FrameAvailable += (s, pcm48k, level) => { /* copy frames to record a voice (mimic) */ };
```

Privacy: with `TransmitOnlyWhenFocused` (default on) nothing is sent while the game window isn't focused — incoming
voice keeps playing (keep *Run In Background* on in Player Settings). `speaker.BlockedByFocus` tells the UI.

Testing: the LAN test scene builder (dev project) adds voice to the test player — **V** push-to-talk,
**R** walkie-talkie, **M** cycle mic mode. Two instances on one PC share the microphone — only the focused window
transmits, so click into the window you want to talk from.

## Steam (`BIG.Unity.Networking.Steam`)

Rooms are Steam lobbies, the game runs over FishySteamworks P2P through the Steam relay (no ports, no NAT problems).

Setup:
1. Install Steamworks.NET (see Installation). FishySteamworks is bundled in the package (BSD-2, with a `PeerToPeer`
   property added) — don't import another copy.
2. *Create > BIG > Networking > Steam Provider* — **App ID** (480 "Spacewar" for development), **Game Id** (lobby filter;
   games sharing an App ID like Spacewar don't see each other's lobbies), **Restart Through Steam** for release builds.
3. NetworkManager: add **FishySteamworks** (or put it in Multipass next to Tugboat for LAN + Steam), assign the Steam
   provider to `NetworkSession`.
4. The Steam client must be running. The provider owns `SteamAPI.Init`/`Shutdown` and `RunCallbacks` — no SteamManager
   needed (and don't add one). Outside Steam (editor, local builds) the App ID is passed through the `SteamAppId`
   environment variable — no `steam_appid.txt`.

What you get:
* **Identity** — `PlayerId` = SteamID64, display name = Steam persona. The host takes each client's identity from the
  Steam P2P connection itself (can't be spoofed).
* **Rooms** — `Host()` creates a lobby (Public / FriendsOnly / Private from `SessionSettings.Visibility`);
  `Search()` returns this game's public lobbies **plus lobbies your friends are in** (friends-only lobbies never show in
  Steam's public list); `SetJoinable(false)` hides the lobby and refuses joins.
* **Invites** — `NetworkSession.Instance.Platform.ShowInviteDialog()` opens the Steam overlay invite dialog. Accepting
  an invite / "Join game" on a friend joins automatically — also when it starts the game (`+connect_lobby <id>`).
* **Rich presence** — friends see "In a joinable game" and get "Join game" while the room is joinable.

Testing needs two Steam accounts on two PCs (one account can't join itself). The dev project has
*BIG > Networking > Build Steam Test Scene*. The Steam overlay doesn't work in the editor — test invites from builds.

## Writing a provider

Implement `INetworkProvider` (platform + `ISessionService` + transport binding) and a `NetworkProviderAsset`
that creates it. FishNet initializes its transport once, so the provider does not add transports — it finds its
own on the NetworkManager (`TransportManager.GetTransport<T>()`, Multipass-aware) and configures it in
`PrepareHost` / `PrepareClient`. See `Lan/` for the reference implementation.

## License

MIT — see [LICENSE.md](LICENSE.md). Required third-party packages: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
