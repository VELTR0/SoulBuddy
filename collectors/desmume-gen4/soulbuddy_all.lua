-- SoulBuddy: one DeSmuME entry script for collector, battle tracking and overlays.
-- Load only this file in DeSmuME.
-- It combines all gui.register callbacks instead of letting them overwrite each other.

if gui == nil or gui.register == nil then
    error("Diese DeSmuME-Version unterstützt gui.register nicht.")
end

local native_gui_register = gui.register
local registered_callbacks = {}

-- Temporarily collect every callback registered by the collector and overlay.
-- DeSmuME normally keeps only the last gui.register callback.
gui.register = function(callback)
    if type(callback) == "function" then
        registered_callbacks[#registered_callbacks + 1] = callback
    end
end

local loaded, load_error = pcall(function()
    dofile "soulbuddy_live.lua"
end)

-- Restore DeSmuME's native API before running anything.
gui.register = native_gui_register

if not loaded then
    error("SoulBuddy konnte nicht geladen werden: " .. tostring(load_error))
end

if #registered_callbacks == 0 then
    error("SoulBuddy hat keinen DeSmuME-Frame-Callback registriert.")
end

-- The original Gen 4 collector infers battle state by checking copied Pokemon
-- structures. HGSS keeps those copies around after leaving a battle, which makes
-- that heuristic sticky: SoulBuddy remains in battle and keeps the last opponent.
-- Use HGSS's dedicated battle-state instruction instead. read_pokemon_words()
-- calls check_is_in_battle() dynamically, so this fixes both battle start and end
-- without changing the proven party/opponent memory layouts.
local HGSS_BATTLE_STATE_ADDRESS = 0x02247612
local HGSS_BATTLE_STATE_ACTIVE = 0x2801

local function hgss_is_in_battle()
    local success, value = pcall(memory.readword, HGSS_BATTLE_STATE_ADDRESS)
    return success and value == HGSS_BATTLE_STATE_ACTIVE
end

function check_is_in_battle(addr, pid)
    return hgss_is_in_battle()
end

-- Every callback is isolated. A broken overlay can therefore never stop
-- party/box collection or the other SoulBuddy callbacks.
local function run_all_soulbuddy_callbacks()
    for index, callback in ipairs(registered_callbacks) do
        local success, callback_error = pcall(callback)
        if not success then
            print(
                "[SoulBuddy] Callback " .. tostring(index) ..
                " fehlgeschlagen: " .. tostring(callback_error))
        end
    end
end

native_gui_register(run_all_soulbuddy_callbacks)

print("[SoulBuddy] Collector, Kampftracking und Overlay sind gemeinsam aktiv.")
print("[SoulBuddy] HGSS-Kampfstatus nutzt die dedizierte Battle-State-Adresse.")
