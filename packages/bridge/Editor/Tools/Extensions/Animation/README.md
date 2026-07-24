# Animation domain pack

Phase 12.4 — seven typed tools for Godot 4.3+ `AnimationPlayer` authoring (player → library → animation → track → key). The hardest P12 pack; mirrors the P12.1–P12.3 packs' folder layout, registration shape, gate policy, and group assignment.

## Group + tool roster

Group id: **`animation`** (default-on: false — activate via `godot_open_mcp_manage_tools`).

| Tool | Mutating | Gate | Purpose |
|---|---|---|---|
| `godot_open_mcp_animation_defaults` | no | off | Recommended starter length + loop mode (pure helper, no scene) |
| `godot_open_mcp_animation_player_create` | yes | enforce | Create an `AnimationPlayer` node |
| `godot_open_mcp_animation_library_add` | yes | enforce | Add an empty `AnimationLibrary` registered by name on a player |
| `godot_open_mcp_animation_create` | yes | enforce | Create an `Animation` clip in a named library (auto-creates library when missing) |
| `godot_open_mcp_animation_add_track` | yes | enforce | Add a value / position_3d / rotation_3d / scale_3d track; returns the track index |
| `godot_open_mcp_animation_insert_key` | yes | enforce | Insert a keyframe on a track; returns the key index Godot assigned |
| `godot_open_mcp_animation_get` | no | off | Read libraries / animations / tracks (bounded; keys opt-in) |

## Scope

- `AnimationPlayer` + `AnimationLibrary` + `Animation` resources only — **not** `AnimationTree`, not `Tween`, not `.glb` retargeting.
- Track types in v1: **value**, **position_3d**, **rotation_3d**, **scale_3d** — the four Godot exposes cleanly for create. Blend-shape / method / bezier / audio / animation tracks are deliberately **not** claimed; an agent requesting one gets `unsupported_track_type`.
- No Unity AnimatorController / `.controller` tools (skipped — Godot's catalog is player-centric).
- No `save_path` persistence model — animations live as resources owned by the `AnimationPlayer` in the edited scene and persist on scene save (Godot default). Marking the scene unsaved is the only persistence step.
- No package compile gate — `AnimationPlayer` is an engine module present in every 4.3+ build.

## Resource model

```
AnimationPlayer (Node, child of the scene root or a parent)
  └─ libraries: Dictionary<StringName, AnimationLibrary>   ← AddAnimationLibrary / GetAnimationLibraryList
       └─ AnimationLibrary (Resource)
            └─ animations: Dictionary<StringName, Animation>  ← AddAnimation / GetAnimationList
                 └─ Animation (Resource)
                      ├─ Length : float (seconds)
                      ├─ LoopMode : LoopNone | LoopLinear | LoopPingpong
                      └─ tracks[] : AddTrack / TrackSetPath / TrackInsertKey
                           ├─ type : TypeValue | TypePosition3D | TypeRotation3D | TypeScale3D
                           ├─ path : NodePath (relative to the player's root_node)
                           └─ keys[] : TrackInsertKey(trackIdx, time, Variant, transition)
```

Godot's default library key is the empty string `""`. This pack names libraries `"default"` by convention (the `EffectiveLibrary` fallback in every body) so an agent does not have to know about the empty-string special case. A clip name like `"Idle"` is the key under which the Animation registers in its library.

## Working API call sequence (the Day-1 spike, codified)

The spec's #1 risk was the exact Godot API call sequence. Verified against the Godot 4.3 C# API (docs.godotengine.org/en/4.3):

```csharp
// 1. Player
var player = new AnimationPlayer();
parent.AddChild(player);
player.Owner = sceneRoot;                       // persists in the .tscn on save

// 2. Library (a named slot on the player)
var library = new AnimationLibrary();
player.AddAnimationLibrary("default", library); // registers library under key "default"

// 3. Animation (a named slot in the library)
var anim = new Animation();
anim.Length = 1.0f;                             // seconds (default 1.0)
anim.LoopMode = Animation.LoopMode.LoopLinear;  // or LoopNone (default) / LoopPingpong
library.AddAnimation("Idle", anim);             // registers clip under key "Idle"

// 4. Track (returns the new track's index)
int trackIdx = anim.AddTrack(Animation.TrackType.Value);
anim.TrackSetPath(trackIdx, new NodePath("Sprite2D:position"));
// Value tracks only: continuous (default) / discrete / capture
anim.ValueTrackSetUpdateMode(trackIdx, Animation.UpdateMode.Continuous);

// 5. Key (returns the key's index inside the track)
int keyIdx = anim.TrackInsertKey(trackIdx, /*time*/ 0.0f, /*Variant*/ Variant.From(0.0), /*transition*/ 1.0f);
// Optional: per-key interpolation override (Nearest / Linear / Cubic).
anim.TrackSetKeyInterpolation(trackIdx, keyIdx, Animation.InterpolationType.Linear);

// 6. Read back
int trackCount = anim.GetTrackCount();           // 1
Animation.TrackType t = anim.TrackGetType(0);    // TypeValue
NodePath p = anim.TrackGetPath(0);               // "Sprite2D:position"
int keyCount = anim.TrackGetKeyCount(0);         // 1
float time = anim.TrackGetKeyTime(0, 0);
Variant value = anim.TrackGetKeyValue(0, 0);

// 7. Player-level library enumeration
var libs = player.GetAnimationLibraryList();     // Godot.Collections.Array<StringName>
bool has = player.HasAnimationLibrary("default");
AnimationLibrary lib = player.GetAnimationLibrary("default");
var anims = lib.GetAnimationList();              // Godot.Collections.Array<StringName>
bool hasAnim = lib.HasAnimation("Idle");
Animation clip = lib.GetAnimation("Idle");
```

## Track path format (the #1 failure mode)

Godot resolves track paths **relative to the AnimationPlayer's `root_node`** (an `AnimationMixer` property; default is the player's parent). The path is a `NodePath` with an optional sub-path to the animated property:

| Example | Animates |
|---|---|
| `"Sprite2D:position"` | the `position` Vector2 of a node named Sprite2D |
| `"Sprite2D:position:x"` | the x component of Sprite2D's position (a float) |
| `"Mesh:blend_shapes/Mouth"` | the `blend_shapes/Mouth` property of a Mesh instance (3D) |
| `"Player/Armature/Skeleton3D:bone_pose/Head"` | a bone pose (3D skeleton) |

A path that does not resolve (wrong node name, or animating a property the node does not have) is **accepted by Godot at authoring time** but produces no effect at playback. This pack surfaces the resolved path back in the `add_track` / `get` results so an agent can verify, but does not validate the path against the scene tree (Godot itself does not — the player may animate nodes added later). When in doubt, animate `position` / `rotation` / `scale` on a sibling Node2D / Node3D.

## Key value JSON shapes

`insert_key` parses the `value` field into a typed structure and converts it to a Godot Variant. Supported shapes (spec design decision §4):

| `value` JSON | Variant type | Notes |
|---|---|---|
| `0.5`, `42`, `-1.5` | float | Whole numbers become floats (value tracks treat numbers as floats) |
| `true` / `false` | bool | |
| `"idle_1"` | string | |
| `{"x":1,"y":2}` | Vector2 | Component form (2D position) |
| `{"x":1,"y":2,"z":3}` | Vector3 | Component form (3D position/rotation/scale) |
| `{"r":1,"g":0,"b":0}` | Color | `a` defaults to 1.0 |
| `{"r":1,"g":0,"b":0,"a":0.5}` | Color | explicit alpha |

A type-tagged object is also accepted (`{"type":"vector3","x":…}`) for explicitness. Rotation3D tracks take a Vector3 in **radians** (Godot convention). An unrecognized shape returns `invalid_parameter`.

## Error codes

Reuses the shared Phase 12 vocabulary (`no_edited_scene`, `node_not_found`, `wrong_node_type`, `missing_parameter`, `invalid_parameter`, `paths_hint_required`, `parent_not_found`, `create_failed`). Pack-specific:

| Code | When |
|---|---|
| `library_not_found` | Missing library and auto-create not applicable (read/add-track/insert paths) |
| `animation_not_found` | Unknown clip name |
| `track_not_found` | `track_index` out of range |
| `unsupported_track_type` | `track_type` is not one of value / position_3d / rotation_3d / scale_3d |
| `already_exists` | Duplicate library or animation name |
