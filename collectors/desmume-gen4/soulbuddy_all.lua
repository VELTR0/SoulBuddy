-- SoulBuddy: one DeSmuME entry script for collector, battle tracking and overlays.
-- Load only this file in DeSmuME.
-- It combines all gui.register callbacks instead of letting them overwrite each other.

if gui == nil or gui.register == nil then
    error("Diese DeSmuME-Version unterstützt gui.register nicht.")
end

local native_gui_register = gui.register
local registered_callbacks = {}

-- Temporarily collect every callback registered by the existing collector
-- and integrated overlay. DeSmuME normally keeps only the last callback.
gui.register = function(callback)
    if type(callback) == "function" then
        registered_callbacks[#registered_callbacks + 1] = callback
    end
end

local loaded, load_error = pcall(function()
    dofile "soulbuddy_live.lua"
end)

-- Always restore the native API before reporting an error.
gui.register = native_gui_register

if not loaded then
    error("SoulBuddy konnte nicht geladen werden: " .. tostring(load_error))
end

if #registered_callbacks == 0 then
    error("SoulBuddy hat keinen DeSmuME-Frame-Callback registriert.")
end

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
