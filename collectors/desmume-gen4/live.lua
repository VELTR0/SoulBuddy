local native_dofile = dofile
local legacy_prefix = "soul" .. "buddy_"
local aliases = {}
aliases[legacy_prefix .. "autostart.lua"] = "bootstrap.lua"
aliases[legacy_prefix .. "stream.lua"] = "stream.lua"
aliases["write_" .. legacy_prefix .. "snapshot.lua"] = "snapshot_writer.lua"

dofile = function(path)
    return native_dofile(aliases[path] or path)
end

local state_ok, state_error = pcall(native_dofile, "live_state.lua")
dofile = native_dofile

if not state_ok then
    error(state_error)
end

dofile "overlay.lua"

print("============================================================")
print("[Live] Collector, Kampfstatus und Event-Overlay aktiv.")
print("[Live] No game memory is written by this collector.")
print("============================================================")

return true
