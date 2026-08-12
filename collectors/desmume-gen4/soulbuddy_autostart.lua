-- Starts SoulBuddy before any collector code is initialized and blocks this Lua
-- bootstrap until the selected SoulBuddy run has a ready JSONL event reader.
-- A named mutex inside SoulBuddy prevents duplicate application instances.

local source = debug.getinfo(1, "S").source
if string.sub(source, 1, 1) == "@" then
    source = string.sub(source, 2)
end

local directory = string.match(source, "^(.*)[/\\]") or "."
local project_root = directory .. "/../.."
local runtime_directory = project_root .. "/runtime"
local ready_file_path = runtime_directory .. "/soulbuddy-ready.txt"
local request_file_path = runtime_directory .. "/soulbuddy-request.txt"
local ready_message_printed = false

local function file_exists(path)
    local file = io.open(path, "rb")
    if file == nil then return false end
    file:close()
    return true
end

local function ensure_runtime_directory()
    os.execute(
        'if not exist "' .. runtime_directory ..
        '" mkdir "' .. runtime_directory .. '"'
    )
end

-- Every Lua start gets its own token. An older SoulBuddy runtime can keep writing
-- heartbeats briefly while it shuts down, but those heartbeats cannot release this
-- new collector because their token no longer matches.
ensure_runtime_directory()
local launch_token = tostring(os.time()) .. "-" .. string.format("%.6f", os.clock())

local request_file, request_error = io.open(request_file_path, "w")
if request_file ~= nil then
    request_file:write(launch_token)
    request_file:flush()
    request_file:close()
else
    error("[SoulBuddy] Start-ID konnte nicht geschrieben werden: " .. tostring(request_error))
end

local function soulbuddy_collector_ready()
    local file = io.open(ready_file_path, "r")
    if file == nil then
        if not ready_message_printed then
            print("[SoulBuddy] Warte auf Run-Auswahl und Collector-Bereitschaft ...")
            ready_message_printed = true
        end
        return false
    end

    local ready_token = file:read("*l") or ""
    local heartbeat = tonumber(file:read("*l") or "")
    file:close()

    if ready_token ~= launch_token or heartbeat == nil then
        if not ready_message_printed then
            print("[SoulBuddy] Warte auf Run-Auswahl und Collector-Bereitschaft ...")
            ready_message_printed = true
        end
        return false
    end

    local age = math.abs(os.time() - heartbeat)
    return age <= 5
end

local function yield_while_waiting()
    -- No SoulBuddy collector/live code has been loaded at this point. frameadvance
    -- merely yields control back to DeSmuME so its UI remains responsive while the
    -- user chooses a run in SoulBuddy. If unavailable, fall back to a short Windows
    -- wait without initializing any collector state.
    if emu ~= nil and type(emu.frameadvance) == "function" then
        emu.frameadvance()
        return
    end

    os.execute('ping 127.0.0.1 -n 2 >nul')
end

local candidates = {}
local configured_path = os.getenv ~= nil and os.getenv("SOULBUDDY_EXE") or nil
if configured_path ~= nil and configured_path ~= "" then
    candidates[#candidates + 1] = configured_path
end

-- In a source checkout the Debug build is normally the freshest executable while
-- testing. SOULBUDDY_EXE can always override discovery for a published installation.
candidates[#candidates + 1] = project_root .. "/bin/Debug/net8.0/SoulBuddy.exe"
candidates[#candidates + 1] = project_root .. "/bin/Debug/net8.0/win-x64/SoulBuddy.exe"
candidates[#candidates + 1] = project_root .. "/SoulBuddy.exe"
candidates[#candidates + 1] = project_root .. "/bin/Release/net8.0/SoulBuddy.exe"
candidates[#candidates + 1] = project_root .. "/bin/Release/net8.0/win-x64/SoulBuddy.exe"
candidates[#candidates + 1] = project_root .. "/bin/Release/net8.0/win-x64/publish/SoulBuddy.exe"

local executable = nil
for _, candidate in ipairs(candidates) do
    if file_exists(candidate) then
        executable = candidate
        break
    end
end

if executable == nil then
    error(
        "[SoulBuddy] EXE nicht gefunden. Baue SoulBuddy zuerst oder setze " ..
        "SOULBUDDY_EXE auf den vollständigen EXE-Pfad.")
end

local windows_executable = string.gsub(executable, "/", "\\")
local command = 'cmd /C start "" /B "' .. windows_executable .. '" --from-lua'
local ok, result = pcall(os.execute, command)

if not ok then
    error("[SoulBuddy] Automatischer EXE-Start fehlgeschlagen: " .. tostring(result))
end

print("[SoulBuddy] Setup-Fenster automatisch gestartet/angefordert: " .. executable)

-- This is intentionally synchronous. Nothing after require('game_version') in the
-- collector can initialize until the current SoulBuddy run confirms this launch token.
while not soulbuddy_collector_ready() do
    yield_while_waiting()
end

print("[SoulBuddy] SoulBuddy ist bereit. Collector wird jetzt initialisiert.")
return true
