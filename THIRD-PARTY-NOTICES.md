# Third-party notices

## Redistributed

- **Concentus 2.2.2** (managed C# port of the Opus codec) — `Plugins/Concentus/Concentus.dll`,
  https://github.com/lostromb/concentus, from nuget.org.
  License: BSD-3-Clause (the Opus license) — full text in [Plugins/Concentus/LICENSE.txt](Plugins/Concentus/LICENSE.txt).
  **Games shipping this package must reproduce that notice in their documentation / credits** (binary redistribution clause).
  Its `System.Memory` / `System.Numerics.Vectors` dependencies are provided by Unity's .NET Standard 2.1 shims.

- **FishySteamworks 4.1.1** (Steam transport for FishNet) — `Runtime/Steam/FishySteamworks/`,
  https://github.com/FirstGearGames/FishySteamworks. License: BSD-2-Clause, Copyright (c) 2022 Benjamin Berwick —
  full text in [Runtime/Steam/FishySteamworks/LICENSE.txt](Runtime/Steam/FishySteamworks/LICENSE.txt).
  Modified: a public `PeerToPeer` property added (marked `[BIG.Unity.Networking]`). Binary redistribution must
  reproduce the notice (credits).

## Required, installed separately by the user

- **Steamworks.NET** (optional — Steam provider only) — https://github.com/rlabrecque/Steamworks.NET — MIT.
  Ships Valve's `steam_api` libraries, which are covered by the Steamworks SDK license (Steam partners).

- **FishNet: Networking Evolved** — Big Ice Games fork https://github.com/Big-Ice-Games/FishNet (branch `big`) of
  https://github.com/FirstGearGames/FishNet — free, see its LICENSE.txt.
- **BIG.Unity** — https://github.com/Big-Ice-Games/BIG.Unity — first-party BigIceGames package (MIT);
  its own THIRD-PARTY-NOTICES.md covers the libraries it redistributes (Autofac).

Without FishNet this package does not compile (assembly define constraint).
