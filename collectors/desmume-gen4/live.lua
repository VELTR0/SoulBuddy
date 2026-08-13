require("render_frame_clock").install()

local native_dofile = dofile
local legacy_snapshot_name = "write_soul" .. "buddy_snapshot.lua"

dofile = function(path)
    if path == legacy_snapshot_name then
        return native_dofile("snapshot_writer.lua")
    end
    return native_dofile(path)
end

local state_ok, state_error = pcall(native_dofile, "live_state.lua")
dofile = native_dofile

if not state_ok then
    error(state_error)
end

require("overlay_runtime")

print("============================================================")
print("[Live] Collector, Kampfstatus und Event-Overlay aktiv.")
print("[Live] No game memory is written by this collector.")
print("============================================================")

return true
