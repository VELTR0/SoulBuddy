local json = require("dkjson")

local source = debug.getinfo(1, "S").source
if string.sub(source, 1, 1) == "@" then source = string.sub(source, 2) end
local directory = string.match(source, "^(.*)[/\\]") or "."
local event_path = directory .. "/../../runtime/overlay-events.jsonl"
local read_position = 0
local initialized = false

local M = {}

function M.read()
    if not initialized then
        local initial = io.open(event_path, "r")
        if initial ~= nil then
            read_position = initial:seek("end") or 0
            initial:close()
        end
        initialized = true
    end

    local messages = {}
    local file = io.open(event_path, "r")
    if file == nil then return messages end

    local length = file:seek("end") or 0
    if read_position > length then read_position = 0 end
    file:seek("set", read_position)

    while true do
        local line = file:read("*l")
        if line == nil then break end
        local decoded = json.decode(line)
        if decoded ~= nil and decoded.message ~= nil and decoded.message ~= "" then
            messages[#messages + 1] = {
                message = tostring(decoded.message),
                duration = tonumber(decoded.durationSeconds) or 7
            }
        end
    end

    read_position = file:seek() or length
    file:close()
    return messages
end

return M
