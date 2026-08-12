-- Starts the SoulBuddy desktop process when the DeSmuME collector is loaded.
-- A named mutex inside SoulBuddy prevents duplicate application instances, so this
-- script may safely try to launch it every time the Lua collector starts.

local source = debug.getinfo(1, "S").source
if string.sub(source, 1, 1) == "@" then
    source = string.sub(source, 2)
end

local directory = string.match(source, "^(.*)[/\\]") or "."
local project_root = directory .. "/../.."

local function file_exists(path)
    local file = io.open(path, "rb")
    if file == nil then return false end
    file:close()
    return true
end

local candidates = {}
local configured_path = os.getenv ~= nil and os.getenv("SOULBUDDY_EXE") or nil
if configured_path ~= nil and configured_path ~= "" then
    candidates[#candidates + 1] = configured_path
end

-- Prefer a published/release build, but keep Debug as a convenient development
-- fallback. A manually copied SoulBuddy.exe in the project root also works.
candidates[#candidates + 1] = project_root .. "/SoulBuddy.exe"
candidates[#candidates + 1] = project_root .. "/bin/Release/net8.0/win-x64/publish/SoulBuddy.exe"
candidates[#candidates + 1] = project_root .. "/bin/Release/net8.0/win-x64/SoulBuddy.exe"
candidates[#candidates + 1] = project_root .. "/bin/Release/net8.0/SoulBuddy.exe"
candidates[#candidates + 1] = project_root .. "/bin/Debug/net8.0/win-x64/SoulBuddy.exe"
candidates[#candidates + 1] = project_root .. "/bin/Debug/net8.0/SoulBuddy.exe"

local executable = nil
for _, candidate in ipairs(candidates) do
    if file_exists(candidate) then
        executable = candidate
        break
    end
end

if executable == nil then
    print("[SoulBuddy] EXE nicht gefunden; Collector startet trotzdem weiter.")
    print("[SoulBuddy] Baue SoulBuddy zuerst oder setze SOULBUDDY_EXE auf den vollständigen EXE-Pfad.")
    return false
end

local windows_executable = string.gsub(executable, "/", "\\")
local command = 'cmd /C start "" /B "' .. windows_executable .. '" --from-lua'
local ok, result = pcall(os.execute, command)

if ok then
    print("[SoulBuddy] Desktop-Prozess automatisch gestartet: " .. executable)
    return true
end

print("[SoulBuddy] Automatischer EXE-Start fehlgeschlagen: " .. tostring(result))
return false
