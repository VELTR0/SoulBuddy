-- Starts the SoulBuddy desktop process when the DeSmuME collector is loaded.
-- A named mutex inside SoulBuddy prevents duplicate application instances, so this
-- script may safely try to launch it every time the Lua collector starts.

local source = debug.getinfo(1, "S").source
if string.sub(source, 1, 1) == "@" then
    source = string.sub(source, 2)
end

local directory = string.match(source, "^(.*)[/\\]") or "."
local project_root = directory .. "/../.."
local ready_file_path = project_root .. "/runtime/soulbuddy-ready.txt"
local ready_message_printed = false

local function file_exists(path)
    local file = io.open(path, "rb")
    if file == nil then return false end
    file:close()
    return true
end

-- Called by the callback gate installed from game_version.lua. SoulBuddy refreshes
-- this timestamp several times per second, so a leftover file from a crashed process
-- cannot accidentally release the collector on the next launch.
function soulbuddy_collector_ready()
    local file = io.open(ready_file_path, "r")
    if file == nil then
        if not ready_message_printed then
            print("[SoulBuddy] Warte, bis SoulBuddy für Collector-Daten bereit ist ...")
            ready_message_printed = true
        end
        return false
    end

    local heartbeat = tonumber(file:read("*l") or "")
    file:close()

    if heartbeat == nil then return false end

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
    print("[SoulBuddy] Desktop-Prozess automatisch gestartet/angefordert: " .. executable)
    return true
end

print("[SoulBuddy] Automatischer EXE-Start fehlgeschlagen: " .. tostring(result))
return false
