-- SoulBuddy: one DeSmuME entry script for collector, battle tracking and overlays.
-- Load only this file in DeSmuME.
-- It combines all gui.register callbacks instead of letting them overwrite each other.

if gui == nil or gui.register == nil then
    error("Diese DeSmuME-Version unterstützt gui.register nicht.")
end

local native_gui_register = gui.register
local native_gui_box = gui.box
local native_gui_text = gui.text
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

-- Always restore the native register API before reporting an error.
gui.register = native_gui_register

if not loaded then
    error("SoulBuddy konnte nicht geladen werden: " .. tostring(load_error))
end

if #registered_callbacks == 0 then
    error("SoulBuddy hat keinen DeSmuME-Frame-Callback registriert.")
end

-- The integrated live collector originally draws its message at the bottom edge.
-- Redirect only those exact overlay drawing calls to the upper DS screen.
-- Other collector/game UI drawing calls are passed through unchanged.
if native_gui_box ~= nil then
    gui.box = function(x1, y1, x2, y2, fill, outline)
        if x1 == 28 and y1 == 168 and x2 == 228 and y2 == 190 then
            return native_gui_box(14, 12, 242, 36, fill, outline)
        end
        return native_gui_box(x1, y1, x2, y2, fill, outline)
    end
end

if native_gui_text ~= nil then
    gui.text = function(x, y, text, foreground, background)
        if x == 42 and y == 176 then
            local value = tostring(text or "")
            local prefix = "SoulBuddy: "
            if string.sub(value, 1, string.len(prefix)) == prefix then
                value = string.sub(value, string.len(prefix) + 1)
            end
            return native_gui_text(22, 20, value, foreground, background)
        end
        return native_gui_text(x, y, text, foreground, background)
    end
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
