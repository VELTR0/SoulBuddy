-- SoulBuddy DeSmuME overlay test
-- Load this script in DeSmuME to display a counter from 0 to 10.
-- The overlay disappears automatically afterwards.
-- No game memory is read or written.

local started_at = os.time()
local finished = false

local function draw_countdown_overlay()
    if finished then
        return
    end

    local elapsed = os.time() - started_at
    if elapsed > 10 then
        finished = true
        return
    end

    local text = "SoulBuddy Overlay-Test: " .. tostring(elapsed)

    -- Position near the bottom of the upper DS screen.
    -- The dark background makes the text readable over the game.
    if gui.box ~= nil then
        gui.box(28, 168, 228, 190, "black", "white")
    end

    gui.text(42, 176, text, "white", "black")
end

if gui ~= nil and gui.register ~= nil then
    gui.register(draw_countdown_overlay)
elseif emu ~= nil and emu.registerafter ~= nil then
    emu.registerafter(draw_countdown_overlay)
else
    error("Diese DeSmuME-Version unterstützt weder gui.register noch emu.registerafter.")
end

print("SoulBuddy Overlay-Test gestartet: Zähler 0 bis 10.")
