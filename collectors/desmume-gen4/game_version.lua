-- Set the version of the game you are running in this file.

local autostart_ok, autostart_result = pcall(function()
    return dofile "bootstrap.lua"
end)

if not autostart_ok then
    error("SoulBuddy konnte vor dem Collector-Start nicht gestartet werden: " .. tostring(autostart_result))
end

if autostart_result ~= true then
    error("SoulBuddy ist nicht bereit. Collector wird nicht initialisiert.")
end

-- SoulBuddy requires gui.register and visible overlays must be rendered there.
-- Disable the legacy second frame driver so the combined callbacks cannot alternate
-- between gui.register and emu.registerafter.
if emu ~= nil then
    emu.registerafter = nil
end

local stream_ok, stream_result = pcall(function()
    return dofile "stream.lua"
end)
if not stream_ok then
    print("[Stream] Video-Bridge konnte nicht geladen werden: " .. tostring(stream_result))
end

local gen3_game = 0
local gen3_subgame = 0
local gen4_gen5_game = 2
local gen4_gen5_subgame = 1

return { gen3_game, gen3_subgame, gen4_gen5_game, gen4_gen5_subgame }
