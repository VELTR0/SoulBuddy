-- Starts SoulBuddy before any collector code is initialized and blocks this Lua
-- bootstrap until the selected SoulBuddy run has a ready JSONL event reader.
-- Windows and macOS use different executable names and launch commands.

local source = debug.getinfo(1, "S").source
if string.sub(source, 1, 1) == "@" then
    source = string.sub(source, 2)
end

local directory = string.match(source, "^(.*)[/\\]") or "."
local project_root = directory .. "/../.."
local runtime_directory = project_root .. "/runtime"
local ready_message_printed = false
local stop_notified = false

local directory_separator = package ~= nil and package.config ~= nil
    and string.sub(package.config, 1, 1)
    or "/"
local is_windows = directory_separator == "\\" or
    (os.getenv ~= nil and os.getenv("OS") == "Windows_NT")
local platform_name = is_windows and "Windows" or "macOS/Unix"

local function file_exists(path)
    local file = io.open(path, "rb")
    if file == nil then return false end
    file:close()
    return true
end

local function read_first_line(path)
    local file = io.open(path, "r")
    if file == nil then return nil end
    local value = file:read("*l")
    file:close()
    return value
end

local function ensure_runtime_directory()
    local command
    if is_windows then
        command = 'if not exist "' .. runtime_directory .. '" mkdir "' .. runtime_directory .. '"'
    else
        command = 'mkdir -p "' .. runtime_directory .. '"'
    end
    os.execute(command)
end

-- Every Lua start gets its own token. Besides protecting the startup handshake,
-- this token now scopes all collector/overlay/video runtime files so two DeSmuME
-- instances can run from the same SoulBuddy checkout without overwriting each other.
ensure_runtime_directory()
local launch_token = tostring(os.time()) .. "-" .. string.format("%.6f", os.clock())
local safe_launch_token = string.gsub(launch_token, "[^%w%-_]", "_")

_G.SOULBUDDY_LAUNCH_TOKEN = launch_token
_G.SOULBUDDY_SAFE_LAUNCH_TOKEN = safe_launch_token

local function scoped_runtime_path(file_name)
    local stem, extension = string.match(file_name, "^(.*)(%.[^.]*)$")
    if stem == nil then
        stem = file_name
        extension = ""
    end
    return runtime_directory .. "/" .. stem .. "." .. safe_launch_token .. extension
end

local ready_file_path = scoped_runtime_path("soulbuddy-ready.txt")
local request_file_path = scoped_runtime_path("soulbuddy-request.txt")
local stop_file_path = scoped_runtime_path("soulbuddy-lua-stopped.txt")

-- The existing collector scripts intentionally keep their simple, generic runtime
-- paths. Remap only those three known files for this Lua process. Other io.open calls
-- (ROM helpers, stream files, etc.) remain untouched.
local native_io_open = io.open
_G.SOULBUDDY_NATIVE_IO_OPEN = native_io_open

local scoped_collector_names = {
    ["party.json"] = true,
    ["emulator-events.jsonl"] = true,
    ["overlay-events.jsonl"] = true
}

local function scope_collector_path(path)
    if type(path) ~= "string" then return path end

    local normalized = string.gsub(path, "\\", "/")
    for file_name, _ in pairs(scoped_collector_names) do
        if #normalized >= #file_name and
           string.sub(normalized, -#file_name) == file_name then
            local stem, extension = string.match(file_name, "^(.*)(%.[^.]*)$")
            if stem == nil then
                stem = file_name
                extension = ""
            end
            local scoped_name = stem .. "." .. safe_launch_token .. extension
            return string.sub(path, 1, #path - #file_name) .. scoped_name
        end
    end

    return path
end

io.open = function(path, mode)
    return native_io_open(scope_collector_path(path), mode)
end

local request_file, request_error = io.open(request_file_path, "w")
if request_file ~= nil then
    request_file:write(launch_token)
    request_file:flush()
    request_file:close()
else
    error("[SoulBuddy] Start-ID konnte nicht geschrieben werden: " .. tostring(request_error))
end

-- DeSmuME calls registered exit handlers when the running Lua script is stopped.
-- Signal only this launch token so a newly started Lua run cannot be terminated by
-- the previous script's cleanup callback.
local function notify_soulbuddy_lua_stopped()
    if stop_notified then return end
    stop_notified = true

    local stop_file = io.open(stop_file_path, "w")
    if stop_file ~= nil then
        stop_file:write(launch_token)
        stop_file:write("\n")
        stop_file:write(tostring(os.time()))
        stop_file:flush()
        stop_file:close()
    end

    -- Remove the request only when it still belongs to this script. If another Lua
    -- run has already written a new token, leave that newer request untouched.
    if read_first_line(request_file_path) == launch_token then
        pcall(os.remove, request_file_path)
    end
end

if emu ~= nil and type(emu.registerexit) == "function" then
    local registered = pcall(emu.registerexit, notify_soulbuddy_lua_stopped)
    if not registered then
        print("[SoulBuddy] Warnung: Lua-Stop-Hook konnte nicht registriert werden.")
    end
else
    print("[SoulBuddy] Warnung: Diese DeSmuME-Version bietet keinen emu.registerexit-Hook.")
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
    -- user chooses a run in SoulBuddy.
    if emu ~= nil and type(emu.frameadvance) == "function" then
        local ok = pcall(emu.frameadvance)
        if ok then return end
    end

    if is_windows then
        os.execute('ping 127.0.0.1 -n 2 >nul')
    else
        os.execute('sleep 1')
    end
end

local candidates = {}
local configured_path = os.getenv ~= nil and os.getenv("SOULBUDDY_EXE") or nil
if configured_path ~= nil and configured_path ~= "" then
    -- SOULBUDDY_EXE remains the override name for compatibility, even on macOS.
    candidates[#candidates + 1] = configured_path
end

if is_windows then
    -- In a Windows source checkout the Debug build is normally the freshest executable.
    candidates[#candidates + 1] = project_root .. "/bin/Debug/net8.0/SoulBuddy.exe"
    candidates[#candidates + 1] = project_root .. "/bin/Debug/net8.0/win-x64/SoulBuddy.exe"
    candidates[#candidates + 1] = project_root .. "/SoulBuddy.exe"
    candidates[#candidates + 1] = project_root .. "/bin/Release/net8.0/SoulBuddy.exe"
    candidates[#candidates + 1] = project_root .. "/bin/Release/net8.0/win-x64/SoulBuddy.exe"
    candidates[#candidates + 1] = project_root .. "/bin/Release/net8.0/win-x64/publish/SoulBuddy.exe"
else
    -- dotnet build/publish on macOS creates an apphost without the .exe extension.
    -- Support both Apple Silicon and Intel output folders plus an optional .app bundle.
    candidates[#candidates + 1] = project_root .. "/bin/Debug/net8.0/SoulBuddy"
    candidates[#candidates + 1] = project_root .. "/bin/Debug/net8.0/osx-arm64/SoulBuddy"
    candidates[#candidates + 1] = project_root .. "/bin/Debug/net8.0/osx-x64/SoulBuddy"
    candidates[#candidates + 1] = project_root .. "/SoulBuddy"
    candidates[#candidates + 1] = project_root .. "/SoulBuddy.app/Contents/MacOS/SoulBuddy"
    candidates[#candidates + 1] = project_root .. "/bin/Release/net8.0/SoulBuddy"
    candidates[#candidates + 1] = project_root .. "/bin/Release/net8.0/osx-arm64/SoulBuddy"
    candidates[#candidates + 1] = project_root .. "/bin/Release/net8.0/osx-arm64/publish/SoulBuddy"
    candidates[#candidates + 1] = project_root .. "/bin/Release/net8.0/osx-x64/SoulBuddy"
    candidates[#candidates + 1] = project_root .. "/bin/Release/net8.0/osx-x64/publish/SoulBuddy"
end

local executable = nil
for _, candidate in ipairs(candidates) do
    if file_exists(candidate) then
        executable = candidate
        break
    end
end

if executable == nil then
    error(
        "[SoulBuddy] Programm nicht gefunden für " .. platform_name ..
        ". Baue SoulBuddy zuerst oder setze SOULBUDDY_EXE auf den vollständigen Pfad.")
end

local command
if is_windows then
    local windows_executable = string.gsub(executable, "/", "\\")
    command = 'cmd /C start "" /B "' .. windows_executable ..
        '" --from-lua --lua-token "' .. launch_token .. '"'
else
    command = 'nohup "' .. executable .. '" --from-lua --lua-token "' ..
        launch_token .. '" >/dev/null 2>&1 &'
end

local call_ok, result, reason, code = pcall(os.execute, command)
if not call_ok then
    error("[SoulBuddy] Automatischer Programmstart fehlgeschlagen: " .. tostring(result))
end

-- Lua 5.1 commonly returns a numeric exit status, newer Lua versions can return
-- true/"exit"/0. Treat an explicit non-zero/false result as a launch failure.
local launch_failed = result == false or result == nil or
    (type(result) == "number" and result ~= 0) or
    (result == true and type(code) == "number" and code ~= 0)
if launch_failed then
    error(
        "[SoulBuddy] Automatischer Programmstart fehlgeschlagen (" ..
        tostring(reason or result) .. ", Code " .. tostring(code or result) .. ").")
end

print("[SoulBuddy] Setup-Fenster automatisch gestartet/angefordert (" .. platform_name .. "): " .. executable)

-- This is intentionally synchronous. Nothing after require('game_version') in the
-- collector can initialize until the current SoulBuddy run confirms this launch token.
while not soulbuddy_collector_ready() do
    yield_while_waiting()
end

print("[SoulBuddy] SoulBuddy ist bereit. Collector wird jetzt initialisiert.")
return true
