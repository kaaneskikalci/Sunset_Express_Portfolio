# Sunset Express

> A co-op physics game about carrying a coffin, with a corpse sliding around inside it, to a grave across a hostile level.

<!-- TODO(Kaan): hero GIF, e.g. the team fumbling the coffin across the seesaw bridge. Suggested path: docs/media/hero.gif -->

**Genre:** co-op physics comedy · **Players:** 2, designed to scale to 4 · **Platform:** PC (Steam)
**Tech:** Unity 6 (URP), C#, Fish-Networking, Steamworks.NET · **Team:** 3, ~6 weeks, mid-2026 · **Status:** discontinued Aug 2026

## Pitch

Your crew runs a funeral service that takes the jobs nobody else will: pick a darkly comic contract from the hub's board, lift the coffin and carry it over seesaw bridges, moving platforms and drops. The body inside isn't fixed: tilt the coffin and the corpse slides, dragging the centre of mass with it. Tilt too far and the lid pops open. Lose the body and the funeral report says so. The comedy comes from physics, never from random events.

## Design pillars

These come from the team's GDD. Code comments cite its section numbers throughout.

- **Readable chaos (GDD 1.4).** Every failure has a trigger players can learn, such as a tilt angle, an impact or a sustained condition. Nothing is random.
- **Difficulty from physics, not multipliers (GDD 4.5).** Each character has a fixed muscle force, so the coffin's mass is what slows the team down, and extra carriers genuinely make it lighter.
- **Release always works (GDD 6.5).** Letting go is a panic button that fires instantly, whatever else is happening.
- **Watch the coffin, not the HUD (GDD 13.2).** There is no strain bar, only a shaking hand icon when a grip is close to breaking.
- **No soft-locks.** Damage can destroy the lid but never the coffin body.

<!-- TODO(Kaan): confirm these are the pillars you want to present, or swap in the GDD's own wording. -->

## Core loop

1. **Hub:** read the contract board (brief, difficulty 1–5). The host starts the contract.
2. **Carry:** get the coffin through the level. Players who die respawn next to it.
3. **Bury:** the contract completes only when nobody is holding the coffin and it has settled in the grave, so the team has to let go together.
4. **Report:** time, coffin damage, and whether the corpse actually arrived.

Payment and end-of-job stats were designed but deliberately left out until the economy was settled ([`ContractReport.cs`](Assets/_Game/Scripts/Runtime/GameLoop/ContractReport.cs)).

## Key mechanics

**1. Carrying and grip.** Players hold the coffin through a spring joint, never through parenting. The host measures joint force and hand drift each physics step and breaks the grip past a threshold. A three-tier warning (~50/65/80%) uses amplitude, scale and colour, so it reads without colour.
*Intent:* make callouts ("slow down, I'm slipping!") matter without a meter.
→ [`PlayerGrabber.cs`](Assets/_Game/Scripts/Runtime/Player/PlayerGrabber.cs), [`GripWarningHud.cs`](Assets/_Game/Scripts/Runtime/UI/GripWarningHud.cs)

**2. Corpse simulation.** In *Mode A* the corpse is one weight that slides head-to-foot and shifts the centre of mass. That creates a deliberate feedback loop: tilt, slide, more tilt. In *Mode B*, if the lid is open and the tilt holds past a threshold, the body falls out as a ragdoll, and that loss is permanent.
*Intent:* panic moments that stay predictable, synced as a single float instead of a full ragdoll.
→ [`CorpseSlide.cs`](Assets/_Game/Scripts/Runtime/Coffin/CorpseSlide.cs)

**3. Lid, damage and corpse variants.** A latch in code releases the lid on tilt or a hard impact and re-locks it once the coffin is righted. Damage lowers the release threshold and loosens the hinge. Corpse variants are data assets that bend the rules: a sealed pharaoh, a basketball player who doesn't fit, a sumo wrestler, a wind-blown elderly lady.
*Intent:* the lid is a crisis the team can recover from, and the real penalty is losing the body. Designers add variants without code.
→ [`CoffinLid.cs`](Assets/_Game/Scripts/Runtime/Coffin/CoffinLid.cs), [`CoffinDamage.cs`](Assets/_Game/Scripts/Runtime/Coffin/CoffinDamage.cs), [`CorpseProfile.cs`](Assets/_Game/Scripts/Runtime/Physics/CorpseProfile.cs)

**4. Synchronised jump.** One carrier only manages a weak hop. When everyone jumps together their momentum combines and the coffin lifts. Joints stiffen briefly after a jump so the impulse reaches the coffin.
*Intent:* a coordination check you shout out loud ("3, 2, 1, jump!").
→ [`PlayerProfile.cs`](Assets/_Game/Scripts/Runtime/Physics/PlayerProfile.cs), [`DebugSyncJump.cs`](Assets/_Game/Scripts/Runtime/Debugging/DebugSyncJump.cs)

**5. Physics-driven obstacles.** The seesaw bridge has no tilt logic. It's a hinged rigidbody, and the weight on it does the work. One panicking player can tip everyone off.
→ [`SeesawBridge.cs`](Assets/_Game/Scripts/Runtime/Obstacles/SeesawBridge.cs), [`MovingPlatform.cs`](Assets/_Game/Scripts/Runtime/Obstacles/MovingPlatform.cs)

**Under the hood:** Unity never steps physics on its own (`m_SimulationMode: 2`, [`DynamicsManager.asset`](ProjectSettings/DynamicsManager.asset)). FishNet's tick loop runs the simulation. Players are client-predicted, and the coffin is host-simulated and replicated. All tuning values live in ScriptableObject profiles.

## My role

I was responsible for game design alongside gameplay and network programming: coffin physics, the grab/release/break systems, corpse simulation, player controls and camera, carrying and jumping, and the network architecture with its prediction and synchronisation. Art, level content and part of the design belonged to the rest of the team.

<!-- TODO(Kaan): credit teammates (names/roles) and confirm which folders are yours. Comments in GameLoop/ and UI/ refer to "Kaan's file" in the third person, suggesting a teammate wrote parts of them. -->
<!-- TODO(Kaan): add 1–2 design decisions you personally drove (GDD sections you wrote, playtest calls you made). -->

## Playtest changes (Aug 2026)

Code comments record several changes made after playtests:
- Removed mouse-wheel raise/lower. It added no meaningful feel.
- Split the grip warning from a single 70% threshold into three tiers.
- Corpse ejection now needs a *sustained* tilt. Single violent frames were dumping bodies unreadably.
- Weakened the solo carry-jump and stiffened joints so group jumps actually lift the coffin.

## Why development stopped

The prototype passed its internal Stage 0 feel test. Even so, the team grew less confident that the interaction had enough depth to sustain a full game. An active-ragdoll pivot also proved incompatible with the deterministic, tick-ordered architecture the game relied on. The [technical postmortem](https://kaaneskikalci.com/files/sunset-express-technical-postmortem.pdf) covers the three failed attempts.

<!-- TODO(Kaan): 3–4 bullets on what you learned as a designer. -->

## Media

<!-- TODO(Kaan): no images exist in this repo yet. Add them under docs/media/ and replace the placeholders below. -->
| | |
|---|---|
| *TODO(Kaan): GIF – two players carrying* | *TODO(Kaan): GIF – corpse slides, lid pops* |
| *TODO(Kaan): screenshot – contract board* | *TODO(Kaan): screenshot – funeral report* |

**Case study:** https://kaaneskikalci.com/dev/sunset-express · **Postmortem (PDF):** https://kaaneskikalci.com/files/sunset-express-technical-postmortem.pdf

## Repository guide

**Code-only mirror:** C# source and project settings. Art, scenes, prefabs and FishNet (an Asset Store product) aren't included, so it won't compile. It's here to be read.

| Path (`Assets/_Game/Scripts/Runtime/…`) | Contents |
|---|---|
| [`Coffin/`](Assets/_Game/Scripts/Runtime/Coffin) | Coffin body, lid latch, damage, corpse simulation |
| [`Player/`](Assets/_Game/Scripts/Runtime/Player) | Predicted controller, grab/carry, arm IK, camera |
| [`Physics/`](Assets/_Game/Scripts/Runtime/Physics) | Tuning profiles (coffin, corpse, player, obstacles) |
| [`GameLoop/`](Assets/_Game/Scripts/Runtime/GameLoop) | Contracts, grave/delivery, respawn, lethal zones |
| [`Obstacles/`](Assets/_Game/Scripts/Runtime/Obstacles) · [`Networking/`](Assets/_Game/Scripts/Runtime/Networking) · [`UI/`](Assets/_Game/Scripts/Runtime/UI) | Obstacles · scene flow and Steam lobby · HUD, menus, report |

**GDD:** not included. Code comments are in Turkish and cite GDD sections (e.g. "GDD 4.3") and files like `Docs/GDD/02-coffin-physics.md`.
<!-- TODO(Kaan): publish a GDD excerpt (e.g. docs/gdd/) or link it from the case study. -->

*Internal working name during development: `coffin`. Licence: [MIT](LICENSE), covering the source in this repository only.*
