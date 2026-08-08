-- SoulBuddy temporary HGSS battle-state probe.
-- Loaded internally by soulbuddy_all.lua. Do not load this file manually.
-- It never writes to game memory.

local json = require("dkjson")
local Pokemon = require("pokemon")

local source = debug.getinfo(1, "S").source
if string.sub(source, 1, 1) == "@" then
    source = string.sub(source, 2)
end
local directory = string.match(source, "^(.*)[/\\]") or "."
local event_path = directory .. "/../../runtime/emulator-events.jsonl"

local poll_interval = 0.25
local last_poll_time = 0
local last_diag_signature = ""
local last_reported_battle = nil

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

local function safe_read_dword(address)
    if address == nil or address <= 0 then return 0 end
    local ok, value = pcall(memory.readdwordunsigned, address)
    if not ok or value == nil then return 0 end
    return value
end

local function safe_value(value, fallback)
    if value == nil then return fallback end
    return value
end

local function serialize_pokemon(pokemon)
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

local function valid_pokemon(pokemon)
    return pokemon ~= nil and
        pokemon.speciesId > 0 and pokemon.speciesId <= 493 and
        pokemon.level > 0 and pokemon.level <= 100 and
        pokemon.maxHp > 0 and pokemon.currentHp >= 0 and
        pokemon.currentHp <= pokemon.maxHp
end

local function snapshot_collector_state()
    return {
        pointer = read_upvalue(getPidAddr, "pointer", 0),
        mode = read_upvalue(getPidAddr, "mode", 1),
        submode = read_upvalue(getPidAddr, "submode", 1),
        in_battle = read_upvalue(read_pokemon_words, "in_battle", false)
    }
end

local function restore_collector_state(snapshot)
    write_upvalue(getPidAddr, "pointer", snapshot.pointer)
    write_upvalue(getPidAddr, "mode", snapshot.mode)
    write_upvalue(getPidAddr, "submode", snapshot.submode)
    write_upvalue(read_pokemon_words, "in_battle", snapshot.in_battle)
end

local function read_candidate(current_pointer, candidate_mode, candidate_slot)
    local snapshot = snapshot_collector_state()
    local result = {
        mode = candidate_mode,
        slot = candidate_slot,
        address = 0,
        rawPid = 0,
        battle = false,
        pokemon = nil
    }

    local ok, err = pcall(function()
        write_upvalue(getPidAddr, "pointer", current_pointer)
        write_upvalue(getPidAddr, "mode", candidate_mode)
        write_upvalue(getPidAddr, "submode", candidate_slot)

        result.address = getPidAddr() or 0
        result.rawPid = safe_read_dword(result.address)
        if result.address == 0 then return end

        local words = read_pokemon_words(result.address, Pokemon.word_size_in_party or 118)
        result.battle = read_upvalue(read_pokemon_words, "in_battle", false) == true
        result.pokemon = serialize_pokemon(Pokemon.parse_gen4_gen5(words, false, 4))
    end)

    restore_collector_state(snapshot)
    if not ok then result.error = tostring(err) end
    return result
end

local function read_all_candidates(current_pointer)
    local candidates = {}
    for slot = 1, 6 do
        candidates[#candidates + 1] = read_candidate(current_pointer, 1, slot)
    end
    for mode = 2, 4 do
        for slot = 1, 2 do
            candidates[#candidates + 1] = read_candidate(current_pointer, mode, slot)
        end
    end
    candidates[#candidates + 1] = read_candidate(current_pointer, 5, 1)
    return candidates
end

local function first_party_pokemon(candidates)
    for _, candidate in ipairs(candidates) do
        if candidate.mode == 1 and valid_pokemon(candidate.pokemon) and candidate.pokemon.currentHp > 0 then
            candidate.pokemon.slot = candidate.slot
            return candidate.pokemon
        end
    end
    return nil
end

local function find_mode5_opponent(candidates, own_active)
    for _, candidate in ipairs(candidates) do
        if candidate.mode == 5 and valid_pokemon(candidate.pokemon) then
            if own_active == nil or candidate.pokemon.pid == 0 or candidate.pokemon.pid ~= own_active.pid then
                return candidate.pokemon, candidate
            end
        end
    end
    return nil, nil
end

local function detect_legacy_battle(candidates)
    for _, candidate in ipairs(candidates) do
        if candidate.mode >= 2 and candidate.mode <= 4 and candidate.battle == true then
            return true
        end
    end
    return false
end

local function append_state(state)
    local file = io.open(event_path, "a")
    if file == nil then return end

    local encoded = json.encode({
        protocolVersion = 1,
        type = "player-state",
        timestamp = os.time(),
        generation = 4,
        game = "hgss",
        state = state
    })

    if encoded ~= nil then
        file:write(encoded)
        file:write("\n")
        file:flush()
    end
    file:close()
end

local function build_raw_diagnostics(current_pointer, candidates, mode5_candidate)
    local game = read_upvalue(getGameName, "game", -1)
    local diagnostics = {
        game = game,
        pointer = current_pointer,
        mode5Address = mode5_candidate and mode5_candidate.address or 0,
        mode5RawPid = mode5_candidate and mode5_candidate.rawPid or 0
    }

    -- HGSS (game == 2 in the original collector).
    if game == 2 and current_pointer ~= 0 then
        diagnostics.enemyPointerAddress = current_pointer + 0x37970
        diagnostics.enemyPointer = safe_read_dword(diagnostics.enemyPointerAddress)
        diagnostics.staticFoeAddress = current_pointer + 0x38540
        diagnostics.staticFoePid = safe_read_dword(diagnostics.staticFoeAddress)
        diagnostics.party1Address = current_pointer + 0xD088
        diagnostics.party1Pid = safe_read_dword(diagnostics.party1Address)

        local cloneBase = diagnostics.party1Address + 0x4E9F0
        diagnostics.partyClone0 = safe_read_dword(cloneBase)
        diagnostics.partyClone1 = safe_read_dword(cloneBase + 0x400000)
        diagnostics.partyClone2 = safe_read_dword(cloneBase + 0x800000)
        diagnostics.partyClone3 = safe_read_dword(cloneBase + 0xC00000)
    end

    return diagnostics
end

local function diagnostic_signature(in_battle, opponent, diagnostics)
    return table.concat({
        in_battle and "1" or "0",
        tostring(opponent and opponent.pid or 0),
        tostring(opponent and opponent.speciesId or 0),
        tostring(diagnostics.enemyPointer or 0),
        tostring(diagnostics.staticFoePid or 0),
        tostring(diagnostics.partyClone0 or 0),
        tostring(diagnostics.partyClone1 or 0),
        tostring(diagnostics.partyClone2 or 0),
        tostring(diagnostics.partyClone3 or 0)
    }, ":")
end

local function print_diagnostics(in_battle, opponent, diagnostics)
    print(string.format(
        "[BATTLE-DIAG] state=%s ptr=0x%08X enemyPtr=0x%08X staticFoePid=%u opponent=%s#%d pid=%u clones=%u/%u/%u/%u",
        in_battle and "BATTLE" or "FIELD",
        diagnostics.pointer or 0,
        diagnostics.enemyPointer or 0,
        diagnostics.staticFoePid or 0,
        opponent and opponent.speciesName or "none",
        opponent and opponent.speciesId or 0,
        opponent and opponent.pid or 0,
        diagnostics.partyClone0 or 0,
        diagnostics.partyClone1 or 0,
        diagnostics.partyClone2 or 0,
        diagnostics.partyClone3 or 0
    ))
end

local function poll_battle_state()
    local now = os.clock()
    if now - last_poll_time < poll_interval then return end
    last_poll_time = now

    local ok, err = pcall(function()
        local current_pointer = getPointer() or 0
        local candidates = read_all_candidates(current_pointer)
        local own_active = first_party_pokemon(candidates)
        local opponent, mode5_candidate = find_mode5_opponent(candidates, own_active)
        local in_battle = detect_legacy_battle(candidates)
        local diagnostics = build_raw_diagnostics(current_pointer, candidates, mode5_candidate)
        diagnostics.source = "continuous-probe"
        diagnostics.pollIntervalMs = 250

        local battle_kind = in_battle and opponent ~= nil and "wild" or in_battle and "unknown" or "none"
        append_state({
            timestamp = os.time(),
            locationName = "HGSS location diagnostic pending",
            inBattle = in_battle,
            battleKind = battle_kind,
            activePokemon = own_active,
            opponentPokemon = in_battle and opponent or nil,
            diagnostics = diagnostics
        })

        local signature = diagnostic_signature(in_battle, opponent, diagnostics)
        if signature ~= last_diag_signature or last_reported_battle ~= in_battle then
            print_diagnostics(in_battle, opponent, diagnostics)
            last_diag_signature = signature
            last_reported_battle = in_battle
        end
    end)

    if not ok then
        print("[BATTLE-DIAG] probe error: " .. tostring(err))
    end
end

gui.register(poll_battle_state)
print("[SoulBuddy] Kontinuierliche HGSS-Kampfdiagnose aktiv (250 ms).")
