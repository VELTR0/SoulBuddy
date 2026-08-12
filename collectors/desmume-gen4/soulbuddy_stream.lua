-- SoulBuddy local video bridge for DeSmuME.
-- The upper DS screen is captured as DeSmuME's native GD string and written to a
-- per-Lua-instance runtime file. SoulBuddy can serve that frame locally. Incoming
-- frames are rendered as a 128x96 picture-in-picture on the upper screen.

if gui == nil then
    return false
end

local source = debug.getinfo(1, "S").source
if string.sub(source, 1, 1) == "@" then
    source = string.sub(source, 2)
end

local directory = string.match(source, "^(.*)[/\\]") or "."
local runtime_directory = directory .. "/../../runtime"
local safe_launch_token = rawget(_G, "SOULBUDDY_SAFE_LAUNCH_TOKEN")

local function scoped_runtime_path(file_name)
    if safe_launch_token == nil or safe_launch_token == "" then
        return runtime_directory .. "/" .. file_name
    end

    local stem, extension = string.match(file_name, "^(.*)(%.[^.]*)$")
    if stem == nil then
        stem = file_name
        extension = ""
    end

    return runtime_directory .. "/" .. stem .. "." .. safe_launch_token .. extension
end

local capture_enabled_path = scoped_runtime_path("stream-capture.enabled")
local outgoing_frame_path = scoped_runtime_path("stream-out.gd")
local incoming_frame_path = scoped_runtime_path("stream-in.gd")

local capture_frame_interval = 4 -- ~15 FPS at the normal 60 FPS emulation rate.
local incoming_read_frame_interval = 2
local last_capture_frame = -1000
local last_incoming_read_frame = -1000
local incoming_frame_cache = nil
local capture_warning_printed = false
local overlay_warning_printed = false

local function file_exists(path)
    local file = io.open(path, "rb")
    if file == nil then return false end
    file:close()
    return true
end

local function read_binary_file(path)
    local file = io.open(path, "rb")
    if file == nil then return nil end
    local data = file:read("*a")
    file:close()
    return data
end

local function valid_gd_frame(data)
    if type(data) ~= "string" or #data < 11 then
        return false
    end

    local marker_high = string.byte(data, 1) or 0
    local marker_low = string.byte(data, 2) or 0
    local width = (string.byte(data, 3) or 0) * 256 + (string.byte(data, 4) or 0)
    local height = (string.byte(data, 5) or 0) * 256 + (string.byte(data, 6) or 0)
    local true_color = string.byte(data, 7) or 0

    if marker_high ~= 255 or marker_low ~= 254 or true_color ~= 1 then
        return false
    end

    if width <= 0 or height <= 0 then
        return false
    end

    return #data == 11 + (width * height * 4)
end

local function write_binary_atomic(path, data)
    local temporary_path = path .. ".tmp"
    local file = io.open(temporary_path, "wb")
    if file == nil then
        return false
    end

    file:write(data)
    file:flush()
    file:close()

    pcall(os.remove, path)
    local renamed = os.rename(temporary_path, path)
    if renamed then
        return true
    end

    -- Some Lua/CRT combinations can reject rename replacement. Fall back to a
    -- direct write so streaming still works for the local test.
    local fallback = io.open(path, "wb")
    if fallback == nil then
        pcall(os.remove, temporary_path)
        return false
    end

    fallback:write(data)
    fallback:flush()
    fallback:close()
    pcall(os.remove, temporary_path)
    return true
end

local function current_frame_number()
    if emu ~= nil and type(emu.framecount) == "function" then
        local ok, frame = pcall(emu.framecount)
        if ok and type(frame) == "number" then
            return frame
        end
    end

    return last_capture_frame + capture_frame_interval
end

local function capture_upper_screen(frame)
    if not file_exists(capture_enabled_path) then
        return
    end

    if frame - last_capture_frame < capture_frame_interval then
        return
    end
    last_capture_frame = frame

    if type(gui.gdscreenshot) ~= "function" then
        if not capture_warning_printed then
            print("[SoulBuddy Stream] gui.gdscreenshot ist in dieser DeSmuME-Version nicht verfügbar.")
            capture_warning_printed = true
        end
        return
    end

    local ok, data = pcall(gui.gdscreenshot, "top")
    if not ok or not valid_gd_frame(data) then
        return
    end

    write_binary_atomic(outgoing_frame_path, data)
end

local function refresh_incoming_frame(frame)
    if frame - last_incoming_read_frame < incoming_read_frame_interval then
        return
    end
    last_incoming_read_frame = frame

    local data = read_binary_file(incoming_frame_path)
    if valid_gd_frame(data) then
        incoming_frame_cache = data
    elseif data == nil then
        incoming_frame_cache = nil
    end
end

local function draw_incoming_frame()
    if incoming_frame_cache == nil then
        return
    end

    if type(gui.gdoverlay) ~= "function" then
        if not overlay_warning_printed then
            print("[SoulBuddy Stream] gui.gdoverlay ist in dieser DeSmuME-Version nicht verfügbar.")
            overlay_warning_printed = true
        end
        return
    end

    -- The upper DS screen occupies y=-192..-1 in DeSmuME's Lua GUI coordinate
    -- system. SoulBuddy downsizes incoming frames to 128x96, so x=128/y=-192
    -- places the video in the upper-right quarter of the top screen.
    pcall(gui.gdoverlay, 128, -192, incoming_frame_cache)
end

local function stream_frame_callback()
    local frame = current_frame_number()
    capture_upper_screen(frame)
    refresh_incoming_frame(frame)
    draw_incoming_frame()
end

if type(gui.register) == "function" then
    gui.register(stream_frame_callback)
elseif emu ~= nil and type(emu.registerafter) == "function" then
    emu.registerafter(stream_frame_callback)
else
    print("[SoulBuddy Stream] Kein Frame-Callback verfügbar; Video-Streaming deaktiviert.")
    return false
end

print("[SoulBuddy Stream] Lokale Video-Bridge bereit.")
return true
