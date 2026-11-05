# Changelog

## 1.1.0
- Interpolation follows trailer trains: every trailer hitched to the vehicle or to another trailer (up to 8), not only the first one.

## 1.0.0
- Shared search cache for every mod in `BepInEx\plugins`: calls to `FindObjectsOfType` / `FindObjectsOfTypeAll` are answered by one scene-wide search reused for 4 s (`FindCacheRefresh`), at most one real search per frame, without destroyed objects. Save/load code and searches for disabled objects are left as they were.
- Objects created inside the patched methods are added to the cache at once.
- Rigidbody interpolation on the player, the driven vehicle and its trailer, with the original values restored when turned off.
- All options on by default and switchable in Apocasetter (`[Fixes]` and `[Interpolation]`); `FindCacheExclude` leaves chosen mods alone.
