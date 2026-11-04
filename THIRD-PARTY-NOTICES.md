# Third-party notices

## Redistributed

- **Concentus 2.2.2** (managed C# port of the Opus codec) — `Plugins/Concentus/Concentus.dll`,
  https://github.com/lostromb/concentus, from nuget.org.
  License: BSD-3-Clause (the Opus license) — full text in [Plugins/Concentus/LICENSE.txt](Plugins/Concentus/LICENSE.txt).
  **Games shipping this package must reproduce that notice in their documentation / credits** (binary redistribution clause).
  Its `System.Memory` / `System.Numerics.Vectors` dependencies are provided by Unity's .NET Standard 2.1 shims.

## Required, installed separately by the user

- **FishNet: Networking Evolved** — Big Ice Games fork https://github.com/Big-Ice-Games/FishNet (branch `big`) of
  https://github.com/FirstGearGames/FishNet — free, see its LICENSE.txt.
- **BIG.Unity** — https://github.com/Big-Ice-Games/BIG.Unity — first-party BigIceGames package (MIT);
  its own THIRD-PARTY-NOTICES.md covers the libraries it redistributes (Autofac).

Without FishNet this package does not compile (assembly define constraint).
