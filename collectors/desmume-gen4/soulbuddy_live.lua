-- SoulBuddy HGSS live collector with integrated event overlay.
-- Load only this file in DeSmuME.
-- It reads emulator state, sends party/box/live-battle data to SoulBuddy,
-- and displays SoulBuddy rule events without writing to game memory.

local json = require("dkjson")
dofile "auto_layout_gen4_gen5.lua"

local source = debug.getinfo(1, "S").source
if string.sub(source, 1, 1) == "@" then
    source = string.sub(source, 2)
end

local directory = string.match(source, "^(.*)[/\\]") or "."
local event_path = directory .. "/../../runtime/emulator-events.jsonl"
local overlay_event_path = directory .. "/../../runtime/overlay-events.jsonl"

local original_send_slots = send_slots
local party_cache = {}
local last_console_signature = ""
local last_console_time = 0
local console_interval = 1.0
local last_live_state_signature = ""
local last_live_poll_frame = -1000
local live_poll_frame_interval = 6

-- Integrated overlay queue.
local overlay_read_position = 0
local overlay_read_position_initialized = false
local overlay_queue = {}
local active_overlay_message = nil
local active_overlay_started_at = 0

local function find_upvalue(func, wanted_name)
    if type(func) ~= "function" then return nil, nil end
    local index = 1
    while true do
        local name, value = debug.getupvalue(func, index)
        if name == nil then return nil, nil end
        if name == wanted_name then return index, value end
        index = index + 1
    end
end

local function read_upvalue(func, name, fallback)
    local _, value = find_upvalue(func, name)
    if value == nil then return fallback end
    return value
end

local function write_upvalue(func, name, value)
    local index = find_upvalue(func, name)
    if index == nil then return false end
    debug.setupvalue(func, index, value)
    return true
end

local function safe_value(value, fallback)
    if value == nil then return fallback end
    return value
end

local collector_game = read_upvalue(getGameName, "game", -1)
local collector_subgame = read_upvalue(getGameName, "subgame", -1)
local PokemonClass = read_upvalue(inspect_and_send_boxes, "Pokemon", nil)

local function refresh_collector_metadata()
    collector_game = read_upvalue(getGameName, "game", collector_game)
    collector_subgame = read_upvalue(getGameName, "subgame", collector_subgame)
    if PokemonClass == nil then
        PokemonClass = read_upvalue(inspect_and_send_boxes, "Pokemon", nil)
    end
end

local function append_event(state)
    local file = io.open(event_path, "a")
    if file == nil then
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
    if pokemon == nil or pokemon.is_empty then return nil end
    local data = pokemon:toJsonSerializableTable(4)
    local hp = data.hp or {}
    return {
        speciesId = safe_value(data.species, 0),
        speciesName = safe_value(data.speciesName, "Unknown"),
        nickname = safe_value(data.nickname, ""),
        level = safe_value(data.level, 0),
        currentHp = safe_value(hp.current, 0),
        maxHp = safe_value(hp.max, 0),
        pid = safe_value(data.pid, 0),
        originalTrainerId = safe_value(data.otid, 0),
        originalTrainerSecretId = safe_value(data.otsid, 0),
        locationMet = safe_value(data.locationMet, 0),
        isShiny = safe_value(data.isShiny, false)
    }
end

local function pokemon_text(pokemon)
    if pokemon == nil then return "empty" end
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

local function snapshot_upvalues()
    return {
        pointer = read_upvalue(getPidAddr, "pointer", 0),
        mode = read_upvalue(getPidAddr, "mode", 1),
        submode = read_upvalue(getPidAddr, "submode", 1),
        in_battle = read_upvalue(read_pokemon_words, "in_battle", false)
    }
end

local function restore_upvalues(snapshot)
    write_upvalue(getPidAddr, "pointer", snapshot.pointer)
    write_upvalue(getPidAddr, "mode", snapshot.mode)
    write_upvalue(getPidAddr, "submode", snapshot.submode)
    write_upvalue(read_pokemon_words, "in_battle", snapshot.in_battle)
end

local function safe_read_candidate(candidate_mode, candidate_slot, current_pointer)
    local snapshot = snapshot_upvalues()
    local success, result = pcall(function()
        if PokemonClass == nil then
            return { mode=candidate_mode, slot=candidate_slot, address=0, pokemon=nil, battle=false, error="Pokemon module upvalue not found" }
        end

        write_upvalue(getPidAddr, "pointer", current_pointer)
        write_upvalue(getPidAddr, "mode", candidate_mode)
        write_upvalue(getPidAddr, "submode", candidate_slot)

        local address = getPidAddr()
        if address == nil or address == 0 then
            return { mode=candidate_mode, slot=candidate_slot, address=address or 0, pokemon=nil, battle=false, error="no-address" }
        end

        local word_size = PokemonClass.word_size_in_party or 118
        local words = read_pokemon_words(address, word_size)
        local detected_battle = read_upvalue(read_pokemon_words, "in_battle", false) == true
        local parsed = PokemonClass.parse_gen4_gen5(words, false, 4)
        return {
            mode = candidate_mode,
            slot = candidate_slot,
            address = address,
            pokemon = serializable_pokemon(parsed),
            battle = detected_battle
        }
    end)

    restore_upvalues(snapshot)
    if not success then
        return { mode=candidate_mode, slot=candidate_slot, address=0, pokemon=nil, battle=false, error=tostring(result) }
    end
    return result
end

local function collect_candidates(current_pointer)
    local candidates = {}
    for candidate_mode = 1, 5 do
        local maximum_slot = candidate_mode == 1 and 6 or 2
        for candidate_slot = 1, maximum_slot do
            candidates[#candidates + 1] = safe_read_candidate(candidate_mode, candidate_slot, current_pointer)
        end
    end
    return candidates
end

local function valid_pokemon(pokemon)
    return pokemon ~= nil and
        pokemon.speciesId > 0 and pokemon.speciesId <= 493 and
        pokemon.level > 0 and pokemon.level <= 100 and
        pokemon.maxHp > 0 and pokemon.currentHp >= 0 and
        pokemon.currentHp <= pokemon.maxHp
end

local function detect_battle(candidates)
    for _, candidate in ipairs(candidates) do
        if candidate.mode >= 2 and candidate.mode <= 4 and candidate.battle == true then
            return true
        end
    end
    return false
end

local function find_mode_five_opponent(candidates, own_active)
    for _, candidate in ipairs(candidates) do
        if candidate.mode == 5 and valid_pokemon(candidate.pokemon) and
           (own_active == nil or candidate.pokemon.pid ~= own_active.pid) then
            return candidate.pokemon
        end
    end
    return nil
end

local function candidate_signature(candidates, battle_flag, battle_kind, opponent)
    local parts = { battle_flag and "battle" or "field", battle_kind or "none" }
    if opponent ~= nil then
        parts[#parts + 1] = tostring(opponent.pid)
        parts[#parts + 1] = tostring(opponent.currentHp)
    end
    for _, candidate in ipairs(candidates) do
        local pokemon = candidate.pokemon
        parts[#parts + 1] = table.concat({
            tostring(candidate.mode), tostring(candidate.slot), tostring(candidate.address),
            tostring(candidate.battle), pokemon and tostring(pokemon.speciesId) or "0",
            pokemon and tostring(pokemon.level) or "0", pokemon and tostring(pokemon.currentHp) or "0",
            pokemon and tostring(pokemon.pid) or "0"
        }, ":")
    end
    return table.concat(parts, "|")
end

local function emit_live_state(force)
    refresh_collector_metadata()

    local current_pointer = getPointer() or 0
    local candidates = collect_candidates(current_pointer)
    local battle_flag = detect_battle(candidates)
    local own_active = first_usable_party_pokemon()
    local mode_five_opponent = find_mode_five_opponent(candidates, own_active)
    local battle_kind = "none"
    local opponent = nil

    if battle_flag then
        if mode_five_opponent ~= nil then
            battle_kind = "wild"
            opponent = mode_five_opponent
        else
            battle_kind = "unknown"
        end
    end

    local signature = candidate_signature(candidates, battle_flag, battle_kind, opponent)
    if not force and signature == last_live_state_signature then
        return
    end

    last_live_state_signature = signature

    append_event({
        timestamp = os.time(),
        locationName = "HGSS location diagnostic pending",
        inBattle = battle_flag,
        battleKind = battle_kind,
        activePokemon = own_active,
        opponentPokemon = opponent,
        diagnostics = {
            game = collector_game,
            subgame = collector_subgame,
            pointer = current_pointer,
            candidateCount = #candidates,
            pokemonModuleFound = PokemonClass ~= nil,
            opponentSource = opponent ~= nil and "mode-5" or "none"
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

    local success = original_send_slots(slots_info, generation, selected_game, selected_subgame)
    if contains_party then
        local live_success = pcall(function() emit_live_state(true) end)
        if not live_success then
            -- Live-state errors must never interrupt the normal party collector.
        end
    end
    return success
end

local function poll_live_battle_state()
    local frame = 0
    if emu ~= nil and emu.framecount ~= nil then
        frame = emu.framecount()
    else
        frame = last_live_poll_frame + live_poll_frame_interval
    end

    if frame - last_live_poll_frame < live_poll_frame_interval then
        return
    end
    last_live_poll_frame = frame

    pcall(function() emit_live_state(false) end)
end

local function initialize_overlay_read_position()
    if overlay_read_position_initialized then
        return
    end

    local file = io.open(overlay_event_path, "r")
    if file ~= nil then
        overlay_read_position = file:seek("end") or 0
        file:close()
    else
        overlay_read_position = 0
    end

    overlay_read_position_initialized = true
end

local function read_new_overlay_messages()
    initialize_overlay_read_position()

    local file = io.open(overlay_event_path, "r")
    if file == nil then
        return
    end

    local length = file:seek("end") or 0
    if overlay_read_position > length then
        overlay_read_position = 0
    end

    file:seek("set", overlay_read_position)

    while true do
        local line = file:read("*l")
        if line == nil then
            break
        end

        local decoded = json.decode(line)
        if decoded ~= nil and decoded.message ~= nil and decoded.message ~= "" then
            overlay_queue[#overlay_queue + 1] = {
                message = tostring(decoded.message),
                duration = tonumber(decoded.durationSeconds) or 7
            }
        end
    end

    overlay_read_position = file:seek() or length
    file:close()
end

local function activate_next_overlay_message()
    if active_overlay_message ~= nil or #overlay_queue == 0 then
        return
    end

    active_overlay_message = table.remove(overlay_queue, 1)
    active_overlay_started_at = os.time()
end

local function utf8_characters(value)
    local characters = {}
    local index = 1

    while index <= #value do
        local first_byte = string.byte(value, index) or 0
        local length = 1
        if first_byte >= 240 then
            length = 4
        elseif first_byte >= 224 then
            length = 3
        elseif first_byte >= 192 then
            length = 2
        end

        characters[#characters + 1] = string.sub(value, index, index + length - 1)
        index = index + length
    end

    return characters
end

local function utf8_length(value)
    return #utf8_characters(value)
end

local function split_long_overlay_word(word, maximum_characters)
    local characters = utf8_characters(word)
    local chunks = {}
    local index = 1

    while index <= #characters do
        local last_index = math.min(index + maximum_characters - 1, #characters)
        local chunk = {}
        for character_index = index, last_index do
            chunk[#chunk + 1] = characters[character_index]
        end
        chunks[#chunks + 1] = table.concat(chunk)
        index = last_index + 1
    end

    return chunks
end

local function wrap_overlay_text(text, maximum_characters)
    local lines = {}
    local current_line = ""

    local function flush_current_line()
        if current_line ~= "" then
            lines[#lines + 1] = current_line
            current_line = ""
        end
    end

    for word in string.gmatch(text, "%S+") do
        local pieces = utf8_length(word) > maximum_characters
            and split_long_overlay_word(word, maximum_characters)
            or { word }

        for _, piece in ipairs(pieces) do
            if current_line == "" then
                current_line = piece
            else
                local candidate = current_line .. " " .. piece
                if utf8_length(candidate) <= maximum_characters then
                    current_line = candidate
                else
                    flush_current_line()
                    current_line = piece
                end
            end

            if utf8_length(current_line) >= maximum_characters then
                flush_current_line()
            end
        end
    end

    flush_current_line()

    if #lines == 0 then
        lines[1] = ""
    end

    return lines
end

local function draw_overlay_messages()
    read_new_overlay_messages()
    activate_next_overlay_message()

    if active_overlay_message == nil then
        return
    end

    local elapsed = os.time() - active_overlay_started_at
    if elapsed >= active_overlay_message.duration then
        active_overlay_message = nil
        activate_next_overlay_message()
        if active_overlay_message == nil then
            return
        end
    end

    local text = active_overlay_message.message
    local lines = wrap_overlay_text(text, 40)

    -- DeSmuME maps the upper DS screen to negative Y coordinates (-192..-1).
    -- Use nearly the complete 256-pixel screen width and grow upward for every
    -- additional wrapped line, keeping the notification at the lower screen edge.
    local box_left = 1
    local box_right = 254
    local box_bottom = -4
    local text_left = 6
    local line_height = 10
    local vertical_padding = 4
    local box_height = (#lines * line_height) + (vertical_padding * 2)
    local box_top = box_bottom - box_height

    if gui.box ~= nil then
        gui.box(box_left, box_top, box_right, box_bottom, "black", "white")
    end

    local first_text_y = box_top + vertical_padding
    for index, line in ipairs(lines) do
        gui.text(
            text_left,
            first_text_y + ((index - 1) * line_height),
            line,
            "white",
            "black")
    end
end

if gui ~= nil and gui.register ~= nil then
    gui.register(poll_live_battle_state)
    gui.register(draw_overlay_messages)
elseif emu ~= nil and emu.registerafter ~= nil then
    emu.registerafter(poll_live_battle_state)
    emu.registerafter(draw_overlay_messages)
else
    error("Diese DeSmuME-Version unterstützt weder gui.register noch emu.registerafter.")
end

print("============================================================")
print("[SoulBuddy Live] Collector, Kampfstatus und Event-Overlay aktiv.")
print("[SoulBuddy Live] No game memory is written by this collector.")
print("============================================================")
