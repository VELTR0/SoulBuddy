local event_file_path =
    "C:/Users/pasca/Documents/SoulBuddy/bin/Debug/net8.0/runtime/emulator-events.jsonl"

local function append_event(json_line)
    local file, open_error = io.open(event_file_path, "a")

    if file == nil then
        print("[SoulBuddy] Could not open event file.")
        print("[SoulBuddy] Path: " .. event_file_path)
        print("[SoulBuddy] Error: " .. tostring(open_error))
        return false
    end

    file:write(json_line)
    file:write("\n")
    file:flush()
    file:close()

    return true
end

local timestamp = os.time()

local event =
    '{"protocolVersion":1,' ..
    '"type":"collector-started",' ..
    '"game":"test",' ..
    '"timestamp":' .. tostring(timestamp) ..
    '}'

if append_event(event) then
    print("[SoulBuddy] Test event written successfully.")
    print("[SoulBuddy] File: " .. event_file_path)
else
    print("[SoulBuddy] Test failed.")
end