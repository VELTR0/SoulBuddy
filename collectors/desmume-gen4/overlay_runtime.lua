local overlay_reader = require("overlay_reader")
local overlay_text = require("overlay_text")

local queue = {}
local active = nil
local started_at = 0

local function draw()
    local incoming = overlay_reader.read()
    for _, message in ipairs(incoming) do
        queue[#queue + 1] = message
    end

    if active == nil and #queue > 0 then
        active = table.remove(queue, 1)
        started_at = os.time()
    end
    if active == nil then return end

    if os.time() - started_at >= active.duration then
        active = nil
        if #queue > 0 then
            active = table.remove(queue, 1)
            started_at = os.time()
        end
        if active == nil then return end
    end

    local lines = overlay_text.wrap(active.message, 40)
    local line_height = 10
    local padding = 4
    local bottom = -4
    local top = bottom - ((#lines * line_height) + (padding * 2))

    if gui.box ~= nil then
        gui.box(1, top, 254, bottom, "black", "white")
    end

    for index, line in ipairs(lines) do
        gui.text(
            6,
            top + padding + ((index - 1) * line_height),
            line,
            "white",
            "black")
    end
end

if gui ~= nil and gui.register ~= nil then
    gui.register(draw)
elseif emu ~= nil and emu.registerafter ~= nil then
    emu.registerafter(draw)
else
    error("Diese DeSmuME-Version unterstützt weder gui.register noch emu.registerafter.")
end

return true
