-- Set the version of the game you are running in this file.

-- This is the first collector bootstrap step. Start SoulBuddy and do not allow
-- auto_layout, party/box tracking, live-state tracking or any gui.register callback
-- to initialize until the selected SoulBuddy run has created a ready JSONL reader.
local autostart_ok, autostart_result = pcall(function()
    return dofile "bootstrap.lua"
end)

if not autostart_ok then
    error("SoulBuddy konnte vor dem Collector-Start nicht gestartet werden: " .. tostring(autostart_result))
end

if autostart_result ~= true then
    error("SoulBuddy ist nicht bereit. Collector wird nicht initialisiert.")
end

-- Smooth the tiny no-file window that can occur while the receiver atomically
-- replaces an incoming video frame. Failure is non-fatal; streaming can still run.
local guard_ok, guard_result = pcall(function()
    return dofile "stream_file_guard.lua"
end)
if not guard_ok then
    print("[Stream] Frame-Datei-Guard konnte nicht geladen werden: " .. tostring(guard_result))
end

-- Video is optional. A failure here must never prevent the normal collector,
-- SoulLink sync or rule-event overlay from starting.
local stream_ok, stream_result = pcall(function()
    return dofile "stream.lua"
end)
if not stream_ok then
    print("[Stream] Video-Bridge konnte nicht geladen werden: " .. tostring(stream_result))
end

--for different game versions
-- 1 = Ruby/Sapphire U, 2 = Emerald U, 3 = FireRed/LeafGreen U, 4 = Ruby/Sapphire J, 5 = Emerald J (TODO),
-- 6 = FireRed/LeafGreen J (1360)
local gen3_game = 0

--0: Ruby/FireRed, Emerald
--1: Sapphire/LeafGreen
local gen3_subgame = 0

-- 1 = Diamond/Pearl, 2 = HeartGold/SoulSilver, 3 = Platinum, 4 = Black, 5 = White, 6 = Black 2, 7 = White 2
local gen4_gen5_game = 2

-- 1 = Diamond, HeartGold, Platinum, Black, white, Black 2, White 2
-- 2 = Pearl, SoulSilver
local gen4_gen5_subgame = 1

return { gen3_game, gen3_subgame, gen4_gen5_game, gen4_gen5_subgame }
