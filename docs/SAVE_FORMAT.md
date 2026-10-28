# Satisfactory save file format

This describes the `.sav` layout as implemented in `src/SatisfactoryMapRenderer/Save/`.
It supersedes the old `save_game_doc.txt`, which only covers save version 30 (Update 6).
All numbers are little-endian. Version numbers refer to the **save version** in the header
unless stated otherwise.

| Save version | Game version | Notable change |
|---|---|---|
| 21 | Update 3 | Body is zlib-compressed |
| 28 | Update 5 | Last version with a single object list (no levels) |
| 29 | Update 6 | Body split into sub-levels + persistent level |
| 37 | Update 8 | Unreal Engine 5: blob sizes become 64-bit |
| 38 | Update 8 | World partition grids, per-object save version |
| 41 | Update 8 | Vectors inside structs become doubles |
| 43 | Update 8 | Inventory items carry a dynamic item-state struct |
| 46 | 1.0 | Persistent level stores destroyed actors per level; lightweight buildables |
| 49 | 1.0 | Object headers get a `uint32` flags field |
| 51 | 1.1 | Each sub-level has its own save version |
| 53 | 1.1 | Package/engine version data (enables UE 5.4+ property tags) |
| 57/58 | 1.2 | New player-info-handle serialization |
| 59/60 | 1.2.4, 1.2.4.1 | Data fix-ups on load only; no format change. Verified against dedicated server saves of 1.2.4 (build 502094) and 1.2.4.1 (build 513885) |

## Basic types

* **FString**: `int32 length`, then the characters including a terminating null.
  Positive length means UTF-8/ASCII (length in bytes). Negative length means UTF-16LE (`-length` characters). Zero means an empty string.
* **ObjectReference**: `FString levelName, FString pathName`.
* **Transform (header)**: `float quat[4], float translation[3], float scale[3]`, always 32-bit floats.

## File header

```
int32  headerVersion
int32  saveVersion
int32  buildVersion
FString saveName                        headerVersion >= 14
FString mapName                         "Persistent_Level"
FString mapOptions
FString sessionName
int32  playDurationSeconds
int64  saveDateTime                     .NET ticks, UTC
byte   sessionVisibility
int32  editorObjectVersion              headerVersion >= 7
FString modMetadata; int32 isModded     headerVersion >= 8
FString saveIdentifier                  headerVersion >= 10
int32  isPartitionedWorld               headerVersion >= 11
int32  hashValid; byte[16] md5 if valid headerVersion >= 12
int32  isCreativeModeEnabled            headerVersion >= 13
```

## Compressed chunks

The rest of the file is a series of zlib chunks, each with its own header:

```
uint32 packageFileTag = 0x9E2A83C1
uint32 archiveHeader  = 0x00000000 (v1) or 0x22222222 (v2)
int64  maxChunkSize   = 131072
byte   compressionAlgorithm = 3 (zlib)          v2 only
int64  compressedSize, int64 uncompressedSize
int64  compressedSize, int64 uncompressedSize   (repeated)
byte[compressedSize] zlib stream
```

The v1 header is 48 bytes and the v2 header is 49. Concatenating the inflated chunks gives the **body**.
The old renderer searched for `78 9C` bytes instead of reading these headers, which is fragile.
Reading the headers also allows chunks to be inflated in parallel.

## Body

```
int32 (v<37) / int64 (v>=37)  remaining body size
FSaveObjectVersionData          v >= 53 (see below; provides the UE5 package version)
Grids                           v >= 38
Levels                          (layout depends on version, below)
```

**Grids** (world-partition validation data, v38+). This is the `"None"`/`"MainGrid"` data the old
notes mistook for objects:

```
int32 gridCount
  FString gridName; int32 cellSize; uint32 gridHash
  int32 cellCount
    FString cellName; uint32 cellHash
```

**FSaveObjectVersionData**:
`uint32 version, int32 ue4Version, int32 ue5Version, int32 licensee, uint16 major, minor, patch, uint32 changelist, FString branch, int32 customVersionCount, (guid[16] + int32) × count`.

### Save version < 29 (Update 5 and older): single object list

```
int32 headerCount;  ObjectHeader × headerCount
int32 objectCount;  (int32 size; ObjectData) × objectCount
int32 collectedCount; ObjectReference × collectedCount
```

### Save version ≥ 29: levels

```
int32 sublevelCount
(sublevelCount + 1) × Level   the last one is the persistent level and has no name field
int32 count; ObjectReference × count      unresolved world save data (optional)
```

Each **Level**:

```
FString levelName                          sub-levels only
int32 tocSize   (+ int32 0 if v >= 37)
TOC blob:  int32 headerCount; ObjectHeader × headerCount
           [collectables / destroyed-actor map, if bytes remain]
int32 dataSize  (+ int32 0 if v >= 37)
Data blob: int32 objectCount; per object:
             int32 objectSaveVersion; int32 shouldMigrate    level version >= 38
             int32 size; ObjectData
             int32 hasVersionData; FSaveObjectVersionData    object version >= 53
int32 levelSaveVersion                     sub-levels, v >= 51
Persistent level: destroyed-actor map (v >= 46): int32 n × (FString level; int32 m; ObjectReference × m)
                  older versions: ObjectReference list
Sub-level:        ObjectReference list (collected items)
                  int32 hasVersionData; FSaveObjectVersionData  v >= 53
```

Collected items (power slugs, somersloops, and so on) are **removed** from the object list and appear
only in these lists. Any collectible still in the save is uncollected.

### ObjectHeader

```
int32 type                1 = actor, 0 = component
FString className, FString rootObject, FString instanceName
uint32 objectFlags        level version >= 49
actor:     int32 needTransform; Transform; int32 wasPlacedInLevel
component: FString parentActorName
```

### ObjectData

```
actor only: ObjectReference parent; int32 componentCount; ObjectReference × componentCount
byte serializationControl                  UE 5.4+ tags only (see below)
PropertyList
int32 hasGuid; guid[16] if set
class specific data                        see "Special data"
```

## Properties

A property list is a sequence of tags terminated by the name `None`.

**Classic tags** (all versions up to 1.0, and 1.1+ saves whose objects predate UE 5.4 tags):

```
FString name; FString type; int32 size; int32 arrayIndex
type specific header:
  ArrayProperty/SetProperty: FString innerType
  StructProperty:            FString structName; guid[16]
  BoolProperty:              byte value
  ByteProperty/EnumProperty: FString enumName ("None" = plain byte)
  MapProperty:               FString keyType; FString valueType
byte hasPropertyGuid; guid[16] if set
value (size bytes)
```

**Complete type name tags** (save version ≥ 53 *and* UE5 package version ≥ 1012):

```
FString name
TypeNode: FString name; int32 childCount; TypeNode × childCount   e.g. ArrayProperty(StructProperty(Vector(/Script/CoreUObject)))
int32 size
byte flags: 0x01 arrayIndex follows (int32), 0x02 guid follows, 0x04 extensions follow,
            0x10 bool value is true
value
```

Containers:

* **Array**: `int32 count`, then elements. In the classic format a struct array repeats a full inner tag (name, `StructProperty`, size, index, struct name, guid, guid flag) before the elements.
* **Set / Map**: `int32 removedCount` (normally 0), `int32 count`, then elements (key + value).
  Struct keys of `mSaveData` are `IntVector`. Set elements of `mRemovalLocations` are vectors, and those of `mDestroyedPickups` are GUIDs.
* **Text**: `uint32 flags; int8 historyType`, then per history type (-1 = invariant string, 0 = base, 1/2/3 = format with arguments, 10 = transform, 11 = string table).

Binary (non tagged) structs: `Vector`, `Rotator`, `Vector2D`, `Quat`, `Vector4`, `Box` (doubles from v41), `IntPoint`, `IntVector`,
`LinearColor` (4 floats), `Color` (BGRA bytes), `Guid`, `RailroadTrackPosition` (ObjectReference + 2 floats), `TimerHandle`,
`DateTime`, `FluidBox` (float), `InventoryItem` (ObjectReference + legacy ref or dynamic struct), `ClientIdentityInfo`,
`PlayerInfoHandle`, `SoftObjectPath`. Every other struct is a nested property list.

## Special data (after the property list)

| Class | Data |
|---|---|
| `Build_PowerLine_C` | source + target connection references; U7+ adds two float vectors (cached wire ends) |
| `BP_CircuitSubsystem_C` | `int32 n × (int32 id, ObjectReference circuit)` |
| `BP_GameState_C`, `BP_GameMode_C` | ObjectReference list |
| `FGLightweightBuildableSubsystem` (1.0+) | `int32 runtimeVersion` (v48+), `int32 groups × (ObjectReference class, int32 n × instance)`; instance = double transform, swatch/material/pattern/skin refs, 2 linear colors, paint-finish ref, byte pattern rotation, recipe ref, blueprint-proxy ref, dynamic struct (runtime ≥ 2), player-info handle (runtime ≥ 3) |
| `FGConveyorChainActor*` (1.0+) | belt spline segments and belt items |
| conveyor belts, vehicles, player state | kept as raw bytes (items on belts, physics state, online identity) |

Foundations, walls, ramps and similar pieces are **not** actors in 1.0+ saves. They exist only as lightweight
buildable instances inside the subsystem above.

## Things that are not in the save

* The resource type and purity of ore nodes, the positions of standing trees, and the terrain.
  These come from the game's level data. The renderer infers a node's resource from the extractor
  built on it (its output inventory). The exception are sessions whose game modes (1.2+) change the
  nodes: there every node, well core and well satellite stores what it became in
  `mResourceClassOverride` (resource descriptor) and `mPurityOverride` (`EResourcePurity`), and these
  beat the level data. The settings themselves are on `BP_GameState_C`: `mNodeRandomization`
  (`ENodeRandomizationMode`: `NRM_Strict` = Random, `NRM_BasicReach`, `NRM_AdvancedRich`,
  `NRM_FossilFuelRich`), `mNodePuritySettings` (`ENodePuritySettings`: `NPS_AllPure`, `NPS_Increase`,
  `NPS_AllNormal`, `NPS_Decrease`, `NPS_AllImpure`, `NPS_AllRandom`) and the world seed
  `mNodeRandomizationSeed` (int32). Each is left out when it is the default (no seed chosen, `NRM_None`,
  `NPS_NoChange`); a session with the game's own nodes stores none of them and no overrides. Verified
  against 1.2.4.1 dedicated server saves (build 513885). The map outline comes from the fog-of-war texture
  (`FGMapManager.mFogOfWarRawData`, 512×512 RGBA, exploration in the third byte), which covers the
  same area as the in-game map: X −324 698.83…425 301.83, Y −375 000…375 000 (Unreal units = cm).
