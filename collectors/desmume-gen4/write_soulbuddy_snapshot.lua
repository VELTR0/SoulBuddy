local print_debug_messages = true

local print_debug = require("print_debug")
print_debug = print_debug(print_debug_messages)

local json = require("dkjson")

local function get_script_directory()
    local source = debug.getinfo(1, "S").source

    if string.sub(source, 1, 1) == "@" then
        source = string.sub(source, 2)
    end

    local directory = string.match(source, "^(.*)[/\\]")

    if directory == nil or directory == "" then
        return "."
    end

    return directory
end

local script_directory = get_script_directory()
local runtime_directory = script_directory .. "/../../runtime"

local snapshot_file_path =
    runtime_directory .. "/party.json"

local event_file_path =
    runtime_directory .. "/emulator-events.jsonl"

local change_ids = { 0, 0, 0, 0, 0, 0 }
local box_change_ids = {}

for box = 1, 18 do
    box_change_ids[box] = {}

    for box_slot = 1, 30 do
        box_change_ids[box][box_slot] = 0
    end
end

local function ensure_runtime_directory()
    os.execute(
        'if not exist "' .. runtime_directory ..
        '" mkdir "' .. runtime_directory .. '"'
    )
end

local function write_snapshot(request_body)
    ensure_runtime_directory()

    local file, open_error = io.open(snapshot_file_path, "w")

    if file == nil then
        print("[SoulBuddy] Could not open snapshot file.")
        print("[SoulBuddy] Path: " .. snapshot_file_path)
        print("[SoulBuddy] Error: " .. tostring(open_error))
        return false
    end

    file:write(request_body)
    file:flush()
    file:close()

    return true
end

local function append_event(event)
    ensure_runtime_directory()

    local event_json = json.encode(event)

    if event_json == nil then
        print("[SoulBuddy] Could not encode collector event.")
        return false
    end

    local file, open_error = io.open(event_file_path, "a")

    if file == nil then
        print("[SoulBuddy] Could not open event file.")
        print("[SoulBuddy] Path: " .. event_file_path)
        print("[SoulBuddy] Error: " .. tostring(open_error))
        return false
    end

    file:write(event_json)
    file:write("\n")
    file:flush()
    file:close()

    return true
end

function reset_server()
    local success = write_snapshot("{}")

    if not success then
        print("[SoulBuddy] Failed to reset snapshot.")
    end
end

function get_game_version(gen, game, subgame)
    if gen == 1 then
        return true
    end

    if gen == 2 then
        return true
    end

    if gen < 3 or gen > 5 then
        print("[ERROR] Invalid game generation:", gen)
        return nil
    end

    if gen == 3 then
        if game > 6 then
            print("[ERROR] Invalid game selected for gen 3:", game)
            return nil
        end

        game = game % 3

        return game == 0 and (subgame == 0 and "fr" or "lg")
            or game == 1 and (subgame == 0 and "r" or "s")
            or "e"
    elseif gen == 4 then
        if game > 3 then
            print("[ERROR] Invalid game selected for gen 4:", game)
            return nil
        end

        return game == 1 and (subgame == 1 and "d" or "p")
            or game == 2 and (subgame == 1 and "hg" or "ss")
            or "pt"
    else
        if game < 4 or game > 7 then
            print("[ERROR] Invalid game selected for gen 5:", game)
            return nil
        end

        return game == 4 and "b"
            or game == 5 and "w"
            or game == 6 and "b2"
            or game == 7 and "w2"
    end
end

function write_file(request_body, generation, game_version, slots)
    local pretty_print = string.gsub(request_body, "\n", "\r\n")
    print_debug(pretty_print)

    local is_box_update =
        slots[1] ~= nil and slots[1].box ~= nil

    if not is_box_update and not write_snapshot(request_body) then
        return false
    end

    local event = {
        protocolVersion = 1,
        type = is_box_update and "box-update" or "party-update",
        timestamp = os.time(),
        generation = generation,
        game = game_version,
        slots = slots
    }

    if not append_event(event) then
        return false
    end

    print(
        is_box_update
            and "[SoulBuddy] Box update written."
            or "[SoulBuddy] Party update written."
    )

    if not is_box_update then
        print("[SoulBuddy] Snapshot: " .. snapshot_file_path)
    end
    print("[SoulBuddy] Events: " .. event_file_path)
    print("[SoulBuddy] Updated slots: " .. tostring(#slots))

    return true
end

function send_slots(slots_info, generation, game, subgame)
    local game_version = get_game_version(generation, game, subgame)

    if game_version == nil then
        return true
    end

    local tmp_info = {}

    for _, value in ipairs(slots_info) do
        tmp_info[#tmp_info + 1] = get_slot_data(value, generation)
    end

    if #tmp_info <= 20 then
        local request_body = json.encode(
            tmp_info,
            { indent = print_debug_messages }
        )

        return write_file(
            request_body,
            generation,
            game_version,
            tmp_info
        )
    end

    local index = 1

    while index <= #tmp_info do
        local batch = {}

        for batch_index = 1, 20 do
            batch[batch_index] = tmp_info[index]
            index = index + 1

            if index > #tmp_info then
                break
            end
        end

        local request_body = json.encode(
            batch,
            { indent = print_debug_messages }
        )

        if not write_file(
            request_body,
            generation,
            game_version,
            batch
        ) then
            return false
        end
    end

    return true
end

function get_slot_data(info, generation)
    local box_id = info.box_id
    local slot = info.slot_id
    local pokemon = info.pokemon

    if box_id ~= nil then
        local change_id = box_change_ids[box_id][slot]
        box_change_ids[box_id][slot] = change_id + 1

        return {
            box = box_id,
            slotId = slot,
            changeId = change_id,
            pokemon = pokemon:toJsonSerializableTable(generation)
        }
    end

    local change_id = change_ids[slot]
    change_ids[slot] = change_id + 1

    return {
        slotId = slot,
        changeId = change_id,
        pokemon = pokemon:toJsonSerializableTable(generation)
    }
end
