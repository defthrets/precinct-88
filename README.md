# Precinct 88

A police overhaul for GTA V, in four parts. SHVDN script mod, no asset changes, no external
dependencies. One build runs on both Legacy and Enhanced.

---

## What it does

**The police know what they have been told, not where you are.**

That is the whole mod, and everything below is a consequence of it.

Vanilla GTA V tracks the player. The search radius on the minimap is a presentation layer over a
system that never actually loses you — officers path straight to your position and the circle is
a courtesy. It is why a chase in this game has exactly one tactic, which is to drive until the
meter runs out, and why hiding has never once worked.

Under the hood that behaviour is one coordinate: `SET_PLAYER_WANTED_CENTRE_POSITION`, which
vanilla keeps pinned to you every frame. Precinct 88 stops pinning it. While an officer can see
you it tracks you and a chase behaves like a chase. The moment nobody can, it freezes at the last
place somebody actually did, and the force works outwards from a spot you are no longer standing
on.

Breaking line of sight becomes worth something. So does an alley, so does going indoors, and so
does changing your jacket.

### 1. Ambient patrol

Police come out of a **finite pool of units on a beat**, not out of thin air behind you. The city
is carved into eight districts, each with a station that answers for it and two separate numbers:
how many cars are out, and how ready they are to start something.

Those two are deliberately not the same dial. Davis is high density and low attention — cars
everywhere, none of which care that you are stood on a corner. Rockford Hills is the exact
opposite: you will go a long time without seeing one, and the one you do see will pull over to ask
what you are doing here.

The game's own random police generator is switched off, because it is a *density target* rather
than a spawner — the engine keeps creating squad cars out of view until an area's police presence
is met, which is precisely why one appears behind you on an empty road at three in the morning.

### 2. Dispatch is reassignment, not spawning

**A response is a car that was already somewhere else.** It took nineteen seconds to reach you
because that is how far away it was, and when the call clears it goes back to the district it
belongs to.

The intended consequence: sometimes nobody comes. If the district is empty and the nearest unit
is across the map, then the nearest unit is across the map.

Escalation is by **what you did**, not by how long it has gone on. A traffic stop is two stars
however far you run and never puts a helicopter up. Vanilla gives you one at four stars regardless
of what the four stars were for.

### 3. Contact starts with a reason

Officers stop you for something: a gun in your hand, how you are driving, the plate, or just
standing somewhere that notices. A stop is a scene, not the opening of a firefight — the wanted
level is capped at the arrest level and the officer's combat is held off for the length of it.

**Leaving is allowed**, and that is why it works. Walk off mid-search. Drive off while they are at
your window. Nothing traps you. What happens is that the thing you were being stopped *for* stops
being the thing you are wanted for.

Crimes and causes are kept apart, which vanilla does not do. Shooting somebody is a crime: it is
reported, searched for, and there is nothing to discuss. Doing fifty through Vespucci is a cause:
it gets you stopped, and what happens next is up to you.

Somebody also has to **see it**. Shoot a man in an empty car park at four in the morning in
vanilla and you get three stars, because the crime is the trigger and nobody needed to be there.
Here a witness makes a call, and the call takes a moment. Gunfire is the exception — it is heard,
so it carries.

### 4. Custody

Vanilla's arrest is a fade to black, a fee, and a hospital respawn — the same thing that happens
when you die. Two costumes, one outcome, and no reason to have preferred being arrested to being
shot.

Here you can **give yourself up** (hold `X` by default), which vanilla does not let you do at all.
Then: cuffs, a walk to the car, the ride, and a hold at the station that answers for the district
you were caught in. Weapons go. Contraband goes. There is a fine scaled to what they booked you
for, and a wait — which is the only part anybody argues about, and can be set to zero.

---

## What they know, and what you can beat

Identification is not one bit. The police hold up to five separate things about you, gain them
independently, and lose them independently:

| Tag | What it is | How you beat it |
|---|---|---|
| `FACE` | Somebody got a proper look at you | **Cover it before the crime.** Nothing undoes it afterwards |
| `FIT` | What you were wearing | Change clothes |
| `CAR` | The model *and* the plate | Change vehicle, or get out and walk |
| `GUN` | What it was done with | Nothing — it isn't a description of you |
| `CAM` | There is footage | Nothing — a camera can't be talked to |

A row of icons under the wanted stars shows them: an eye or a magnifier for whether they can see
you or are searching, then one icon per thing they hold.

**Red means they hold it and it still describes you. Grey means they hold it and it is now
wrong** — a grey shirt is the force still looking for a man in the thing you changed out of ten
minutes ago, which is the single most useful fact the game can give you and is invisible in every
other police mod, because none of them model identification as separable pieces. A green question
mark means a crime was called in and nobody could describe you at all.

**State is icons; words are for things that need explaining.** A notification that scrolls away
after four seconds is the wrong medium for something that is true for the next two minutes — the
answer to "what do they have on me" was gone exactly when you were busy being chased. So the HUD
carries states, and a notification is reserved for what an icon cannot say: what was seized, what
you were booked for, why a surrender failed.

The art is generated by `tools/make_icons.py` rather than committed by hand — that file is the
source and the PNGs are build output. It exists because the alternatives do not work: the game
has no handcuffs, eye or camera sprite that `DRAW_SPRITE` can reach, and blip art, which does have
the right pictures, renders in help text and on the map but draws *nothing at all* in a HUD
string.

An officer only acts if *something* still matches. All they ever had was a jacket you've since
changed? He looks straight at you and carries on, because there is nothing left to match you
against.

**A crime with no description is a real state**, and it's the one vanilla can't represent. Gunfire
heard through a wall gets police converging on the street with a location and nothing else — and
they are told to ignore you personally, so they search around a man they have no reason to look at
twice. Shoot again where one of them can see you and it flips instantly.

**Witnesses give what they actually got.** An officer gets everything. A civilian across the street
gets your clothes and your car but couldn't pick you out of a line-up — only one close enough gets
your face. Anyone who only *heard* it gives a location. And a witness has to survive to make the
call, which players work out very quickly.

**Cameras are the witness that isn't a person** — the counter to doing everything where nobody is
standing. They're found as world props rather than from a coordinate list, so they're wherever
Rockstar actually put them, and a map mod that adds a shop adds its camera for free.

**Your criminal profile outlives the session.** Not a morality score — it doesn't care *what* you
do, only *how*. Commit a crime and drive off and it barely moves; shoot the clerk and then the
first officer through the door and it moves a long way, and the response runs colder for a while
after. It bleeds off in real time, so leaving it alone is always the way back.

**A crime scene stays warm.** In vanilla the meter runs out and the street is as innocent as it was
that morning — you can drive back to the body and park. Here, coming back within a few minutes of
something serious can put it back on you, once.

> Most of this section mirrors what Rockstar showed of GTA VI's wanted system in *An Extended Look*
> (August 2026) — witness-or-alarm reporting, icons for what police know, a hollow star for a crime
> with no suspect, and an RDR2-style profile of how violently you work. The design was arrived at
> here independently and then extended to match.

---

## The panel — `F11`

Two jobs, and the second is the reason it exists.

Changing settings is the obvious one: every knob in the ini, live, written straight back to
`Precinct88.ini` on each change with your comments and formatting intact. Arrows move and change,
Enter toggles, Backspace closes.

The other is the **status block along the bottom**, which prints what the police currently think:

```
Davis                              density 0.95   attention 0.30
units out 3   on a call 1              vanilla police suppressed
searching 118m -- shots fired -- has FACE+FIT+CAR, you match CAR
profile violent (0.61)                            face uncovered
```

That block matters more than the settings do. The whole premise of this mod is a gap between where
you are and where the police *believe* you are — and none of that is visible from the pavement.
Without a readout, "the search works" and "the search silently does nothing" look identical. It is
also the fastest way to find out whether the beat is producing cars at all.

`F11` was checked rather than picked — it is free on both installs here. Change it with
`[General] MenuKey`.

---

## Install

1. [ScriptHookV](http://www.dev-c.com/gtav/scripthookv/) and
   [ScriptHookVDotNet 3](https://github.com/scripthookvdotnet/scripthookvdotnet/releases) —
   **3.9 or newer**.
2. Drop the `scripts` folder into your GTA V directory.

You should end up with:

```
Grand Theft Auto V\
  scripts\
    Precinct88.dll
    Precinct88.ini
    Precinct88\
      stations.json
```

Everything is tunable in `Precinct88.ini`, which is never overwritten by an update. All four
systems switch off independently — if you only want the beat patrol, turn the other three off.

---

## Compatibility

**LSPDFR** — Precinct 88 stands down automatically when it finds LSPDFR installed. LSPDFR owns
dispatch and the wanted system and puts you on the other side of all of this; two systems giving
officers contradictory orders every frame half works in a way nobody can diagnose from inside the
game. Set `StandDownForLspdfr=false` to run both anyway.

**Other mods that hand out stars** — a wanted level this mod did not issue is *adopted* rather
than suppressed: an incident opens at your position with a severity read off the star count, and
from there it searches, carries a description, and can be lost like any other. Which means the
search mechanic works for crimes Precinct 88 has never heard of, and heist/mission/callout mods
installed beside it keep working.

**[Hoodrich](https://github.com/defthrets/hoodrich)** — integrated. See below.

---

## The Hoodrich bridge

Hoodrich's own ambient patrol has been removed; this mod owns it now. The two talk over a
**late-bound reflection seam** (`Precinct88.Api.Dispatch`), so neither references the other,
either can be updated alone, and both run standalone.

With both installed:

- **One law hold.** Both mods had a counted hold for taking the police off during a scene. Two
  counted holds that do not know about each other is the exact bug either was written to prevent,
  one layer up — whichever finishes first hands the police back to the other, so a booking ending
  during a gang war brings a helicopter to the war. Hoodrich now forwards to this one.
- **A bust is a report, not a handful of stars.** It becomes a narcotics call at your position
  that has to be searched for, with a description out. The alley behind the corner is worth
  something.
- **A search costs product.** Precinct 88 does not know what a gram is; it asks Hoodrich, which
  takes it and says what it took.
- **The beat stands down** for a gang war, a raid or a job, through the shared hold. Cars already
  out stay where they are — one vanishing off the street the moment a war starts is more
  conspicuous than one driving through it.

Why reflection and not a shared interop DLL: a GTA `scripts\` folder is one assembly resolution
namespace. Two mods referencing a third assembly must agree about its exact version forever, and
when they stop the failure is a `TypeLoadException` at load with no log — because the thing that
would have written the log is the thing that did not load. Late binding cannot lose that fight.

---

## Known rough edges

- **Station coordinates have not been walked in-game yet.** Nothing spawns *at* one — cars come
  out of the nearest real road node found with the game's own pathfinding, so a point half a block
  off produces a car on the right street rather than a car in a wall. The **desk** positions, where
  a booked player is stood, are the ones that would show an error. They live in
  `scripts\Precinct88\stations.json` and fixing one needs no rebuild.
- **Animation clips are guarded, not verified.** Dictionary names are the most fragile strings in
  a GTA mod — wrong ones fail silently and the usual `while (!loaded) Yield()` idiom turns that
  into a hang. Everything here is time-boxed, so a wrong name costs a missing animation and a line
  in the log, never a freeze.
- Nothing in this build has been run in-game. It compiles clean and every native it calls was
  verified against SHVDN 3.9's `Hash` enum by reflecting the assembly.

---

## Building

`build.ps1` drives a self-contained Roslyn compiler rather than `dotnet build` — one library, no
NuGet, no project file, no SDK. The toolchain is not in this repo; it looks in `.\tools\` and then
`..\hoodrich\tools\`, or pass `-Tools <path>`.

```
.\build.ps1
.\build.ps1 -Deploy -Target Both
.\build.ps1 -Package
```

---

by spitmux
