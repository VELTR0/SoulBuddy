-- SoulBuddy overlay message receiver for DeSmuME.
-- Reads messages written by SoulBuddy and displays each one for four seconds.
-- Uses the exact box, position and text style of overlay_countdown_test.lua.

local json = require("dkjson")

local source = debug.getinfo(1, "S").source
if string.sub(source, 1, 1) == "@" then
    source = string.sub(source, 2)
end

local directory = string.match(source, "^(.*)[/\\]") or "."
local overlay_event_path = directory .. "/../../runtime/overlay-events.jsonl"

local read_position = 0
local read_position_initialized = false
local queue = {}
local active_message = nil
local active_started_at = 0

local function initialize_read_position()
    if read_position_initialized then
        return
    end

    local file = io.open(overlay_event_path, "r")
    if file ~= nil then
        read_position = file:seek("end") or 0
        file:close()
    else
        read_position = 0
    end

    read_position_initialized = true
end

local function read_new_messages()
    initialize_read_position()

    local file = io.open(overlay_event_path, "r")
    if file == nil then
        return
    end

    local length = file:seek("end") or 0
    if read_position > length then
        read_position = 0
    end

    file:seek("set", read_position)

    while true do
        local line = file:read("*l")
        if line == nil then
            break
        end

        local decoded = json.decode(line)
        if decoded ~= nil and decoded.message ~= nil and decoded.message ~= "" then
            queue[#queue + 1] = {
                message = tostring(decoded.message),
                duration = tonumber(decoded.durationSeconds) or 4
            }
        end
    end

    read_position = file:seek() or length
    file:close()
end

local function activate_next_message()
    if active_message ~= nil or #queue == 0 then
        return
    end

    active_message = table.remove(queue, 1)
    active_started_at = os.time()
end

local function draw_overlay_messages()
    read_new_messages()
    activate_next_message()

    if active_message == nil then
        return
    end

    local elapsed = os.time() - active_started_at
    if elapsed >= active_message.duration then
        active_message = nil
        activate_next_message()
        if active_message == nil then
            return
        end
    end

    local text = "SoulBuddy: " .. active_message.message

    if gui.box ~= nil then
        gui.box(28, 168, 228, 190, "black", "white")
    end

    gui.text(42, 176, text, "white", "black")
end

if gui ~= nil and gui.register ~= nil then
    gui.register(draw_overlay_messages)
elseif emu ~= nil and emu.registerafter ~= nil then
    emu.registerafter(draw_overlay_messages)
else
    error("Diese DeSmuME-Version unterstützt weder gui.register noch emu.registerafter.")
end
