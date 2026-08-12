-- Set the version of the game you are running in this file.

-- Start the SoulBuddy desktop process whenever the DeSmuME collector is loaded.
-- Startup failures must never prevent the emulator collector itself from loading.
local autostart_ok, autostart_error = pcall(function()
    dofile "soulbuddy_autostart.lua"
end)
if not autostart_ok then
    print("[SoulBuddy] Autostart-Script konnte nicht geladen werden: " .. tostring(autostart_error))
end

-- soulbuddy_all.lua temporarily replaces gui.register so it can combine all
-- callbacks into one DeSmuME callback. Wrap that temporary register function here,
-- before auto_layout and soulbuddy_live register their callbacks. The callbacks stay
-- dormant until SoulBuddy's JSONL reader has initialized and publishes a fresh
-- heartbeat. This preserves first_run and prevents the initial party snapshot from
-- being written before SoulBuddy can consume it.
if gui ~= nil and gui.register ~= nil then
    local register_callback = gui.register
    gui.register = function(callback)
        if type(callback) ~= "function" then
            return register_callback(callback)
        end

        return register_callback(function()
            if type(soulbuddy_collector_ready) == "function" and
               not soulbuddy_collector_ready() then
                return
            end

            return callback()
        end)
    end
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
