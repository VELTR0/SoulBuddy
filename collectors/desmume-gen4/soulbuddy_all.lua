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

gui.register = function(callback)
    if type(callback) == "function" then
        registered_callbacks[#registered_callbacks + 1] = callback
    end
end

local loaded, load_error = pcall(function()
    dofile "soulbuddy_live.lua"
end)

gui.register = native_gui_register

if not loaded then
    error("SoulBuddy konnte nicht geladen werden: " .. tostring(load_error))
end

if #registered_callbacks == 0 then
    error("SoulBuddy hat keinen DeSmuME-Frame-Callback registriert.")
end

-- Correct HGSS live-battle memory layout. This only reads emulator memory.
local pointer = 0
local mode = 1
local submode = 1
local in_battle = false

local function hgss_is_in_battle()
    return memory.readword(0x02247612) == 0x2801
end

function getPidAddr()
    if pointer == nil or pointer == 0 then
        pointer = getPointer() or 0
    end
    if pointer == 0 then return 0 end

    if mode == 5 then
        return pointer + 0x35AC4
    end

    if mode >= 2 and mode <= 4 then
        local enemy_root = memory.readdword(pointer + 0x352F4)
        if enemy_root == nil or enemy_root < 0x02000000 or enemy_root > 0x02400000 then
            return 0
        end

        local offset = 0x7A0
        if mode == 3 then
            offset = offset + 0xB60
        elseif mode == 4 then
            offset = offset + 0x5B0
        end
        return enemy_root + offset + 0xEC * (submode - 1)
    end

    return pointer + 0xD094 + 0xEC * (submode - 1)
end

function check_is_in_battle(addr, pid)
    return hgss_is_in_battle()
end

function read_pokemon_words(addr, num_words)
    if addr == nil or addr == 0 then
        in_battle = false
        return { 0, 0 }
    end

    local pid = memory.readdword(addr)
    in_battle = hgss_is_in_battle()
    local bytes = memory.readbyterange(addr, num_words * 2)
    local words = {}
    words[1] = bit.rshift(pid, 16)
    words[2] = bit.band(pid, 0xFFFF)

    for i = 5, #bytes, 2 do
        words[#words + 1] = bytes[i] + bit.lshift(bytes[i + 1], 8)
    end
    return words
end

-- Compatibility redirect for older lower-screen overlay coordinates.
if native_gui_box ~= nil then
    gui.box = function(x1, y1, x2, y2, fill, outline)
        if x1 == 28 and y1 == 168 and x2 == 228 and y2 == 190 then
            return native_gui_box(14, -30, 242, -6, fill, outline)
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
            return native_gui_text(22, -22, value, foreground, background)
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
