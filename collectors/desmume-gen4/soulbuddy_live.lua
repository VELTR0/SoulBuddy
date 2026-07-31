-- Run this file in DeSmuME instead of auto_layout_gen4_gen5.lua.
-- It adds diagnostic player-state events without changing the game memory.

local json = require("dkjson")
dofile "auto_layout_gen4_gen5.lua"

local source = debug.getinfo(1, "S").source
if string.sub(source, 1, 1) == "@" then source = string.sub(source, 2) end
local directory = string.match(source, "^(.*)[/\\]") or "."
local event_path = directory .. "/../../runtime/emulator-events.jsonl"

local original_send_slots = send_slots
local party_cache = {}
local last_party_update = nil
local rapid_updates = 0

local function append_event(state)
    local file = io.open(event_path, "a")
    if file == nil then return end
    file:write(json.encode({
        protocolVersion = 1,
        type = "player-state",
        timestamp = os.time(),
        generation = 4,
        game = "hgss",
        state = state
    }))
    file:write("\n")
    file:flush()
    file:close()
end

local function live_pokemon(pokemon)
    if pokemon == nil or pokemon.is_empty then return nil end
    local data = pokemon:toJsonSerializableTable(4)
    local hp = data.hp or {}
    return {
        speciesId = data.species or 0,
        speciesName = data.speciesName or "Unknown",
        nickname = data.nickname or "",
        level = data.level or 0,
        currentHp = hp.current or 0,
        maxHp = hp.max or 0
    }
end

local function active_pokemon()
    for slot = 1, 6 do
        local pokemon = party_cache[slot]
        if pokemon ~= nil and not pokemon.is_empty then
            local data = pokemon:toJsonSerializableTable(4)
            local hp = data.hp or {}
            if (hp.current or 0) > 0 then return live_pokemon(pokemon) end
        end
    end
    return nil
end

function send_slots(slots_info, generation, game, subgame)
    local contains_party = false
    for _, info in ipairs(slots_info) do
        if info.box_id == nil and info.slot_id ~= nil then
            contains_party = true
            party_cache[info.slot_id] = info.pokemon
        end
    end

    local success = original_send_slots(slots_info, generation, game, subgame)

    if contains_party then
        local now = os.clock()
        if last_party_update ~= nil and now - last_party_update < 0.8 then
            rapid_updates = math.min(rapid_updates + 1, 4)
        else
            rapid_updates = 0
        end
        last_party_update = now

        append_event({
            timestamp = os.time(),
            locationName = "Aufenthaltsort wird ermittelt",
            inBattle = rapid_updates >= 2,
            battleKind = rapid_updates >= 2 and "unknown" or "none",
            activePokemon = active_pokemon(),
            diagnostics = {
                rapidUpdates = rapid_updates,
                game = game or -1,
                subgame = subgame or -1
            }
        })
    end

    return success
end

print("[SoulBuddy Live] Diagnostic live-state collector active.")
