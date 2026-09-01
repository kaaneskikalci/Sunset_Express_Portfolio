# Sunset Express — gameplay source

**Sunset Express** was a cooperative physics game about carrying an unstable coffin —
built for two players, designed to scale to four. Three of us worked on it in Unity
over roughly six weeks in mid-2026, targeting a PC release on Steam.

**Development was discontinued in August 2026.** The prototype passed its internal
Stage 0 feel test, but we became less confident that the interaction produced enough
depth to sustain the larger game, and an active-ragdoll pivot turned out to be
incompatible with the deterministic architecture the game depended on.

I was responsible for game design alongside gameplay and network programming: the
coffin physics, the grab / release / break systems, corpse simulation, player controls
and camera, carrying and jumping behaviour, and the network architecture with its
prediction and synchronisation. Art, level content and part of the design belonged to
the rest of the team.

- **Case study:** https://kaaneskikalci.com/dev/sunset-express
- **Technical postmortem:** https://kaaneskikalci.com/files/sunset-express-technical-postmortem.pdf

*Internal working / repository name during development: `coffin`.*

---

## What this repository is

This is a **code-only mirror**: the C# source and the project settings, nothing else.

It does **not** contain art, audio, scenes or prefabs, and it does **not** bundle
FishNet — the networking library is a Unity Asset Store product and is not mine to
redistribute. The original working repository carried it in-tree, which is fine for a
private team repo and not fine for a public one.

So this will not compile as-is. It is here to be read, not built.

## Where to look

| Path | What lives there |
|---|---|
| `Assets/_Game/Scripts` | Coffin physics, grab/break systems, corpse simulation, player controls, camera, networking |
| `ProjectSettings/DynamicsManager.asset` | `m_SimulationMode: 2` — Unity never steps physics on its own |

That last one is the whole architecture in one line. Networked physics is an ordering
problem before it is a physics problem, so the simulation step was handed entirely to
FishNet's tick loop rather than left to Unity's own fixed update.

Authority is split deliberately. Players are client-predicted and reconciled against
the server. The coffin is not predicted at all — it is simulated by the host and
replicated to clients, because handing authority to whoever grabs it falls apart the
moment two people hold it at once. Both halves depend on every machine stepping the
simulation in the same order, which is what the tick pipeline provides.

The postmortem linked above covers the tick pipeline, the force-band locomotion model,
the shared-body connection between two carriers, the synchronised-jump fix, and the
three failed attempts at the ragdoll pivot.

## Licence

See [LICENSE](LICENSE). Applies to the source in this repository only.
