-- Run this file in DeSmuME instead of auto_layout_gen4_gen5.lua.
-- It adds diagnostic live-encounter output without writing to game memory.
--
-- Test outside battle, in a wild battle and in a trainer battle. Copy the
-- complete blocks beginning with [SoulBuddy Live] and send them back.

local json = require("dkjson")
dofile "auto_layout_gen4_gen5.lua"

local source = debug.getinfo(1, "S").source
if string.sub(source, 1, 1) == "@" then
    source = string.sub(source, 2)
end

local directory = string.match(source, "^(.*)[/\\]") or "."
local event_path = directory .. "/../../runtime/emulator-events.jsonl"

local original_send_slots = send_slots
local party_cache = {}
local last_console_signature = ""
local last_console_time = 0
local console_interval = 1.0

local function safe_value(value, fallback)
    if value == nil then
        return fallback
    end

    return value
end

local function append_event(state)
    local file = io.open(event_path, "a")

    if file == nil then
        print("[SoulBuddy Live] Could not open event file: " .. event_path)
        return false
    end

    local encoded = json.encode({
        protocolVersion = 1,
        type = "player-state",
        timestamp = os.time(),
        generation = 4,
        game = "hgss",
        state = state
    })

    if encoded == nil then
        file:close()
        return false
    end

    file:write(encoded)
    file:write("\n")
    file:flush()
    file:close()
    return true
end

local function serializable_pokemon(pokemon)
    if pokemon == nil or pokemon.is_empty then
        return nil
    end

    local data = pokemon:toJsonSerializableTable(4)
    local hp = data.hp or {}

    return {
        speciesId = safe_value(data.species, 0),
        speciesName = safe_value(data.speciesName, "Unknown"),
        nickname = safe_value(data.nickname, ""),
        level = safe_value(data.level, 0),
        currentHp = safe_value(hp.current, 0),
        maxHp = safe_value(hp.max, 0),
        pid = safe_value(data.pid, 0)
    }
end

local function pokemon_text(pokemon)
    if pokemon == nil then
        return "empty"
    end

    local name = pokemon.speciesName
    if pokemon.nickname ~= nil and pokemon.nickname ~= "" then
        name = pokemon.nickname .. " (" .. pokemon.speciesName .. ")"
    end

    return string.format(
        "%s #%d Lv.%d HP %d/%d PID %s",
        name,
        pokemon.speciesId,
        pokemon.level,
        pokemon.currentHp,
        pokemon.maxHp,
        tostring(pokemon.pid)
    )
end

local function first_usable_party_pokemon()
    for slot = 1, 6 do
        local pokemon = serializable_pokemon(party_cache[slot])

        if pokemon ~= nil and pokemon.currentHp > 0 then
            pokemon.slot = slot
            return pokemon
        end
    end

    return nil
end

local function safe_read_candidate(candidate_mode, candidate_slot)
    local previous_mode = mode
    local previous_submode = submode
    local previous_in_battle = in_battle

    mode = candidate_mode
    submode = candidate_slot

    local success, result = pcall(function()
        local address = getPidAddr()

        if address == nil or address == 0 then
            return {
                mode = candidate_mode,
                slot = candidate_slot,
                address = address or 0,
                pokemon = nil,
                error = "no-address"
            }
        end

        local words = read_pokemon_words(
            address,
            Pokemon.word_size_in_party
        )
        local parsed = Pokemon.parse_gen4_gen5(words, false, 4)

        return {
            mode = candidate_mode,
            slot = candidate_slot,
            address = address,
            pokemon = serializable_pokemon(parsed)
        }
    end)

    mode = previous_mode
    submode = previous_submode
    in_battle = previous_in_battle

    if not success then
        return {
            mode = candidate_mode,
            slot = candidate_slot,
            address = 0,
            pokemon = nil,
            error = tostring(result)
        }
    end

    return result
end

local function collect_candidates()
    local candidates = {}

    -- Mode 1 is the normal party. Modes 2-4 are the battle layouts used by
    -- the original tracker. Mode 5 is included as a diagnostic control.
    for candidate_mode = 1, 5 do
        local maximum_slot = candidate_mode == 1 and 6 or 2

        for candidate_slot = 1, maximum_slot do
            candidates[#candidates + 1] =
                safe_read_candidate(candidate_mode, candidate_slot)
        end
    end

    return candidates
end

local function candidate_signature(candidates, battle_flag)
    local parts = { battle_flag and "battle" or "field" }

    for _, candidate in ipairs(candidates) do
        local pokemon = candidate.pokemon
        parts[#parts + 1] = table.concat({
            tostring(candidate.mode),
            tostring(candidate.slot),
            tostring(candidate.address),
            pokemon and tostring(pokemon.speciesId) or "0",
            pokemon and tostring(pokemon.level) or "0",
            pokemon and tostring(pokemon.currentHp) or "0",
            pokemon and tostring(pokemon.pid) or "0"
        }, ":")
    end

    return table.concat(parts, "|")
end

local function print_diagnostic(candidates, battle_flag, own_active)
    print("============================================================")
    print("[SoulBuddy Live] HGSS encounter diagnostic")
    print("[SoulBuddy Live] game=" .. tostring(game) ..
        " subgame=" .. tostring(subgame) ..
        " pointer=" .. string.format("0x%08X", pointer or 0))
    print("[SoulBuddy Live] in_battle=" .. tostring(battle_flag))
    print("[SoulBuddy Live] fallback active party Pokemon: " ..
        pokemon_text(own_active))
    print("[SoulBuddy Live] Candidate memory layouts:")

    for _, candidate in ipairs(candidates) do
        local address_text = string.format("0x%08X", candidate.address or 0)
        local suffix = candidate.error ~= nil
            and (" error=" .. candidate.error)
            or ""

        print(string.format(
            "[SoulBuddy Live] mode=%d slot=%d addr=%s -> %s%s",
            candidate.mode,
            candidate.slot,
            address_text,
            pokemon_text(candidate.pokemon),
            suffix
        ))
    end

    print("[SoulBuddy Live] Copy this complete block after testing:")
    print("[SoulBuddy Live] 1) outside battle")
    print("[SoulBuddy Live] 2) wild battle")
    print("[SoulBuddy Live] 3) trainer battle")
    print("============================================================")
end

local function choose_probable_battle_pokemon(candidates)
    for _, candidate in ipairs(candidates) do
        if candidate.mode >= 2 and candidate.mode <= 4 and
           candidate.pokemon ~= nil and
           candidate.pokemon.speciesId > 0 and
           candidate.pokemon.speciesId <= 493 and
           candidate.pokemon.level > 0 and
           candidate.pokemon.level <= 100 then
            return candidate.pokemon
        end
    end

    return nil
end

local function emit_live_diagnostic()
    local battle_flag = in_battle == true
    local candidates = collect_candidates()
    local own_active = first_usable_party_pokemon()
    local probable_opponent = battle_flag
        and choose_probable_battle_pokemon(candidates)
        or nil
    local signature = candidate_signature(candidates, battle_flag)
    local now = os.clock()

    if signature ~= last_console_signature or
       now - last_console_time >= console_interval then
        print_diagnostic(candidates, battle_flag, own_active)
        last_console_signature = signature
        last_console_time = now
    end

    append_event({
        timestamp = os.time(),
        locationName = "HGSS location diagnostic pending",
        inBattle = battle_flag,
        battleKind = battle_flag and "unknown" or "none",
        activePokemon = own_active,
        opponentPokemon = probable_opponent,
        diagnostics = {
            game = game or -1,
            subgame = subgame or -1,
            pointer = pointer or 0,
            candidateCount = #candidates
        }
    })
end

function send_slots(slots_info, generation, selected_game, selected_subgame)
    local contains_party = false

    for _, info in ipairs(slots_info) do
        if info.box_id == nil and info.slot_id ~= nil then
            contains_party = true
            party_cache[info.slot_id] = info.pokemon
        end
    end

    local success = original_send_slots(
        slots_info,
        generation,
        selected_game,
        selected_subgame
    )

    if contains_party then
        local diagnostic_success, diagnostic_error =
            pcall(emit_live_diagnostic)

        if not diagnostic_success then
            print("[SoulBuddy Live] Diagnostic error: " ..
                tostring(diagnostic_error))
        end
    end

    return success
end

print("============================================================")
print("[SoulBuddy Live] HGSS console diagnostic collector active.")
print("[SoulBuddy Live] Run THIS file instead of auto_layout_gen4_gen5.lua.")
print("[SoulBuddy Live] No game memory is written by this diagnostic.")
print("============================================================")
