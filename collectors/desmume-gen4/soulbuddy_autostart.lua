-- Starts the SoulBuddy desktop process when the DeSmuME collector is loaded.
-- A named mutex inside SoulBuddy prevents duplicate application instances, so this
-- script may safely try to launch it every time the Lua collector starts.

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
local launch_token = tostring(os.time()) .. "-" ..
    string.gsub(string.format("%.6f", os.clock()), "\\.", "_")

local request_file, request_error = io.open(request_file_path, "w")
if request_file ~= nil then
    request_file:write(launch_token)
    request_file:flush()
    request_file:close()
else
    print("[SoulBuddy] Start-ID konnte nicht geschrieben werden: " .. tostring(request_error))
    launch_token = nil
end

-- Called by the callback gate installed from game_version.lua. SoulBuddy writes the
-- launch token it adopted plus a fresh timestamp. Both must match this Lua start.
function soulbuddy_collector_ready()
    if launch_token == nil then return false end

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
    local ready = age <= 5
    if ready and ready_message_printed then
        print("[SoulBuddy] SoulBuddy ist bereit. Collector wird jetzt freigegeben.")
        ready_message_printed = false
    end
    return ready
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
    print("[SoulBuddy] EXE nicht gefunden; Collector wartet auf eine SoulBuddy-Instanz.")
    print("[SoulBuddy] Baue SoulBuddy zuerst oder setze SOULBUDDY_EXE auf den vollständigen EXE-Pfad.")
    return false
end

local windows_executable = string.gsub(executable, "/", "\\")
local command = 'cmd /C start "" /B "' .. windows_executable .. '" --from-lua'
local ok, result = pcall(os.execute, command)

if ok then
    print("[SoulBuddy] Setup-Fenster automatisch gestartet/angefordert: " .. executable)
    return true
end

print("[SoulBuddy] Automatischer EXE-Start fehlgeschlagen: " .. tostring(result))
return false
