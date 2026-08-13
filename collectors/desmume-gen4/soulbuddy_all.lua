-- SoulBuddy: one DeSmuME entry script for collector, battle tracking and overlays.
-- Load only this file in DeSmuME.
-- It combines all gui.register callbacks instead of letting them overwrite each other.

if gui == nil or gui.register == nil then
    error("Diese DeSmuME-Version unterstützt gui.register nicht.")
end

local native_gui_register = gui.register
local registered_callbacks = {}

-- Temporarily collect every callback registered by the collector and overlay.
-- DeSmuME normally keeps only the last gui.register callback.
gui.register = function(callback)
    if type(callback) == "function" then
        registered_callbacks[#registered_callbacks + 1] = callback
    end
end

local loaded, load_error = pcall(function()
    dofile "live.lua"
end)

-- Restore DeSmuME's native API before running anything.
gui.register = native_gui_register

if not loaded then
    error("SoulBuddy konnte nicht geladen werden: " .. tostring(load_error))
end

if #registered_callbacks == 0 then
    error("SoulBuddy hat keinen DeSmuME-Frame-Callback registriert.")
end

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

local function read_upvalue(func, wanted_name, fallback)
    local _, value = find_upvalue(func, wanted_name)
    if value == nil then return fallback end
    return value
end

local function write_upvalue(func, wanted_name, value)
    local index = find_upvalue(func, wanted_name)
    if index == nil then return false end
    debug.setupvalue(func, index, value)
    return true
end

-- HGSS keeps the old copied Pokemon structures around after leaving a battle.
-- Therefore they are not a reliable battle flag. Use the dedicated battle-state
-- instruction instead.
local HGSS_BATTLE_STATE_ADDRESS = 0x02247612
local HGSS_BATTLE_STATE_ACTIVE = 0x2801

local function hgss_is_in_battle()
    local success, value = pcall(memory.readword, HGSS_BATTLE_STATE_ADDRESS)
    return success and value == HGSS_BATTLE_STATE_ACTIVE
end

-- Mode 5 is the collector's static wild-opponent slot. It already points to the
-- opponent's PKM structure, so read_pokemon_words() must NOT translate it to the
-- party battle-copy offset. Modes 2-4 still use the real HGSS battle flag.
function check_is_in_battle(addr, pid)
    local collector_mode = read_upvalue(getPidAddr, "mode", 1)
    if collector_mode == 5 then
        return false
    end

    return hgss_is_in_battle()
end

-- Live field-location reading for HGSS.
-- The game resolves its field/save base through a two-step pointer chain.
local HGSS_GLOBAL_POINTER_ADDRESS = 0x02000BA8
local HGSS_VERSION_POINTER_OFFSET = 0x20
local HGSS_CURRENT_MAP_OFFSET = 0x25FE4
local HGSS_MAX_MAP_ID = 0x21B

local function is_main_ram_pointer(value)
    return type(value) == "number" and value >= 0x02000000 and value < 0x02400000
end

local function safe_read_dword(address)
    if type(address) ~= "number" or address <= 0 then return nil end

    if memory.readdwordunsigned ~= nil then
        local ok, value = pcall(memory.readdwordunsigned, address)
        if ok and value ~= nil then return value end
    end

    local ok, value = pcall(memory.readdword, address)
    if not ok then return nil end
    return value
end

local function safe_read_word(address)
    if type(address) ~= "number" or address <= 0 then return nil end

    if memory.readwordunsigned ~= nil then
        local ok, value = pcall(memory.readwordunsigned, address)
        if ok and value ~= nil then return value end
    end

    local ok, value = pcall(memory.readword, address)
    if not ok then return nil end
    return value
end

local HGSS_AREA_BY_MAP = {}

local function set_area(map_id, area)
    HGSS_AREA_BY_MAP[map_id] = area
end

local function set_range(first_id, last_id, area)
    for map_id = first_id, last_id do
        HGSS_AREA_BY_MAP[map_id] = area
    end
end

for map_id = 9, 26 do
    set_area(map_id, "route_" .. tostring(map_id - 8))
end

local exact_areas = {
    [6]="bell_tower", [7]="burned_tower", [8]="ruins_of_alph",
    [27]="route_22", [28]="route_24", [29]="route_25", [30]="route_26",
    [31]="route_27", [32]="route_28", [33]="route_29", [34]="route_30",
    [35]="route_31", [36]="route_32", [37]="route_33", [38]="route_34",
    [39]="route_35", [40]="route_36", [41]="route_37", [42]="route_38",
    [43]="route_39", [44]="route_42", [45]="route_43", [46]="route_44",
    [47]="route_45", [48]="route_46",
    [49]="pallet_town", [50]="viridian_city", [51]="pewter_city",
    [52]="cerulean_city", [53]="lavender_town", [54]="vermilion_city",
    [55]="celadon_city", [56]="fuchsia_city", [57]="cinnabar_island",
    [58]="indigo_plateau", [59]="saffron_city",
    [73]="violet_city", [74]="azalea_town", [75]="cianwood_city",
    [76]="goldenrod_city", [77]="olivine_city", [87]="mahogany_town",
    [88]="lake_of_rage", [89]="blackthorn_city", [90]="mt_silver",
    [91]="route_19", [92]="route_20", [93]="route_21", [94]="route_40",
    [95]="route_41", [96]="national_park", [97]="route_31", [98]="route_32",
    [99]="union_cave", [100]="azalea_town", [101]="route_35",
    [102]="route_35", [103]="route_36", [104]="route_36",
    [105]="ecruteak_city", [106]="digletts_cave", [107]="mt_moon",
    [108]="rock_tunnel", [109]="pal_park", [110]="sprout_tower",
    [111]="bell_tower", [112]="goldenrod_city", [113]="ruins_of_alph",
    [114]="slowpoke_well", [115]="olivine_lighthouse", [116]="team_rocket_hq",
    [117]="ilex_forest", [118]="goldenrod_city", [119]="mt_mortar",
    [120]="ice_path", [121]="whirl_islands", [122]="mt_silver_cave",
    [123]="dark_cave", [124]="victory_road", [125]="dragons_den",
    [126]="tohjo_falls", [127]="route_30", [132]="route_42",
    [133]="mahogany_town", [134]="route_29", [135]="violet_city",
    [136]="azalea_town", [137]="goldenrod_city", [138]="olivine_city",
    [139]="cianwood_city", [140]="mahogany_town", [141]="blackthorn_city",
    [142]="route_43", [143]="route_30", [144]="cherrygrove_city",
    [145]="cerulean_cave", [146]="seafoam_islands", [147]="viridian_forest",
    [148]="route_9", [149]="violet_city", [150]="national_park",
    [151]="route_47", [152]="route_48", [171]="route_34", [172]="route_38",
    [175]="ecruteak_city", [176]="dark_cave", [177]="slowpoke_well",
    [178]="victory_road", [179]="victory_road", [180]="azalea_town",
    [181]="slowpoke_well", [214]="route_39", [215]="route_39",
    [216]="ecruteak_city", [217]="burned_tower", [218]="ruins_of_alph",
    [219]="goldenrod_city", [240]="olivine_city", [241]="cianwood_city",
    [245]="route_43", [246]="mahogany_town", [253]="dragons_den",
    [279]="route_47", [288]="dragons_den", [294]="lake_of_rage",
    [295]="lake_of_rage", [296]="route_26", [297]="route_26",
    [298]="tohjo_falls", [322]="route_27", [330]="olivine_city",
    [331]="route_34", [342]="cliff_cave", [366]="route_40",
    [367]="olivine_city", [368]="mahogany_town", [369]="blackthorn_city",
    [384]="new_bark_town", [385]="cianwood_city", [386]="vermilion_city",
    [387]="vermilion_city", [388]="route_10", [389]="route_6",
    [390]="route_8", [391]="route_5", [392]="route_15",
    [411]="battle_frontier", [414]="route_2", [415]="route_16",
    [416]="route_20", [417]="route_2", [418]="route_2", [419]="route_2",
    [420]="route_2", [421]="route_16", [422]="route_16", [423]="route_18",
    [424]="route_19", [425]="route_11", [440]="route_25",
    [441]="goldenrod_city", [442]="celadon_city", [443]="celadon_city",
    [444]="celadon_city", [445]="saffron_city", [446]="olivine_lighthouse",
    [447]="goldenrod_city", [448]="mt_moon", [449]="mt_moon",
    [450]="cerulean_cave", [451]="cerulean_cave", [452]="rock_tunnel",
    [466]="route_10", [467]="route_10", [468]="route_5", [469]="route_5",
    [470]="route_6", [484]="route_10", [486]="whirl_islands",
    [487]="national_park", [488]="national_park", [489]="route_10",
    [490]="ruins_of_alph", [491]="ruins_of_alph", [492]="ruins_of_alph",
    [493]="route_7", [494]="lavender_town", [495]="cerulean_city",
    [508]="cinnabar_island", [509]="cinnabar_island", [510]="route_28",
    [511]="route_3", [512]="route_3", [513]="mt_moon", [514]="mt_silver",
    [515]="mt_silver", [517]="route_5", [518]="mt_moon",
    [519]="goldenrod_city", [520]="saffron_city", [521]="sinjoh_ruins",
    [522]="sinjoh_ruins", [523]="sinjoh_ruins", [524]="embedded_tower",
    [525]="embedded_tower", [526]="embedded_tower", [527]="viridian_city",
    [532]="route_5", [533]="route_12", [536]="goldenrod_city",
    [537]="celadon_city", [539]="indigo_plateau"
}

for map_id, area in pairs(exact_areas) do
    set_area(map_id, area)
end

set_range(60, 66, "new_bark_town")
set_range(67, 72, "cherrygrove_city")
set_range(78, 86, "ecruteak_city")
set_range(128, 131, "ecruteak_city")
set_range(153, 154, "union_cave")
set_range(155, 156, "sprout_tower")
set_range(157, 162, "violet_city")
set_range(163, 166, "azalea_town")
set_area(167, "violet_city")
set_area(168, "azalea_town")
set_range(169, 170, "route_32")
set_range(173, 174, "safari_zone")
set_range(182, 213, "goldenrod_city")
set_range(220, 225, "olivine_lighthouse")
set_range(226, 231, "olivine_city")
set_range(232, 236, "cianwood_city")
set_range(237, 239, "ice_path")
set_range(242, 244, "whirl_islands")
set_range(247, 249, "team_rocket_hq")
set_range(250, 252, "mt_mortar")
set_range(254, 278, "battle_frontier")
set_range(280, 287, "national_park")
set_range(289, 293, "blackthorn_city")
set_range(299, 306, "indigo_plateau")
set_range(307, 311, "ss_aqua")
set_range(312, 321, "ruins_of_alph")
set_range(323, 327, "ruins_of_alph")
set_range(328, 329, "ss_aqua")
set_range(332, 341, "bell_tower")
set_range(343, 357, "safari_zone")
set_range(358, 365, "vermilion_city")
set_range(370, 383, "celadon_city")
set_range(393, 395, "celadon_city")
set_range(396, 397, "mahogany_town")
set_range(398, 410, "saffron_city")
set_range(412, 413, "goldenrod_city")
set_range(426, 432, "cerulean_city")
set_range(433, 439, "lavender_town")
set_range(453, 458, "seafoam_islands")
set_range(459, 465, "mt_silver_cave")
set_range(471, 477, "pewter_city")
set_range(478, 483, "fuchsia_city")
set_area(485, "fuchsia_city")
set_range(496, 502, "viridian_city")
set_range(503, 507, "pallet_town")
set_range(528, 531, "battle_frontier")
set_range(534, 535, "safari_zone")

local HGSS_AREA_NAMES = {
    bell_tower = "Glockenturm",
    burned_tower = "Turmruine",
    ruins_of_alph = "Alph-Ruinen",
    pallet_town = "Alabastia",
    viridian_city = "Vertania City",
    pewter_city = "Marmoria City",
    cerulean_city = "Azuria City",
    lavender_town = "Lavandia",
    vermilion_city = "Orania City",
    celadon_city = "Prismania City",
    fuchsia_city = "Fuchsania City",
    cinnabar_island = "Zinnoberinsel",
    indigo_plateau = "Indigo-Plateau",
    saffron_city = "Saffronia City",
    new_bark_town = "Neuborkia",
    cherrygrove_city = "Rosalia City",
    violet_city = "Viola City",
    azalea_town = "Azalea City",
    cianwood_city = "Anemonia City",
    goldenrod_city = "Dukatia City",
    olivine_city = "Oliviana City",
    ecruteak_city = "Teak City",
    mahogany_town = "Mahagonia City",
    lake_of_rage = "See des Zorns",
    blackthorn_city = "Ebenholz City",
    mt_silver = "Silberberg",
    national_park = "Nationalpark",
    union_cave = "Einheitshöhle",
    digletts_cave = "Digdas Höhle",
    mt_moon = "Mondberg",
    rock_tunnel = "Felstunnel",
    pal_park = "Park der Freunde",
    sprout_tower = "Knofensa-Turm",
    slowpoke_well = "Flegmon-Brunnen",
    olivine_lighthouse = "Leuchtturm",
    team_rocket_hq = "Rocket-Versteck",
    ilex_forest = "Steineichenwald",
    mt_mortar = "Kesselberg",
    ice_path = "Eispfad",
    whirl_islands = "Strudelinseln",
    mt_silver_cave = "Silberberghöhle",
    dark_cave = "Finsterhöhle",
    victory_road = "Siegesstraße",
    dragons_den = "Drachenhöhle",
    tohjo_falls = "Tohjo-Fälle",
    safari_zone = "Safari-Zone",
    cerulean_cave = "Azuria-Höhle",
    seafoam_islands = "Seeschauminseln",
    viridian_forest = "Vertania-Wald",
    battle_frontier = "Kampfzone",
    ss_aqua = "M.S. Aqua",
    cliff_cave = "Felsenhöhle",
    sinjoh_ruins = "Sinjoh-Ruinen",
    embedded_tower = "Felsenherzturm"
}

local function display_area(area, map_id)
    if area == nil or area == "" then
        return map_id == nil
            and "Aufenthaltsort wird ermittelt"
            or ("Unbekannter Kartenbereich (" .. tostring(map_id) .. ")")
    end

    if string.sub(area, 1, 6) == "route_" then
        return "Route " .. string.sub(area, 7)
    end

    return HGSS_AREA_NAMES[area] or area
end

local function read_current_location()
    local first_pointer = safe_read_dword(HGSS_GLOBAL_POINTER_ADDRESS)
    if not is_main_ram_pointer(first_pointer) then
        return nil, "Aufenthaltsort wird ermittelt", nil
    end

    local field_base = safe_read_dword(first_pointer + HGSS_VERSION_POINTER_OFFSET)
    if not is_main_ram_pointer(field_base) then
        return nil, "Aufenthaltsort wird ermittelt", nil
    end

    local map_address = field_base + HGSS_CURRENT_MAP_OFFSET
    local map_id = safe_read_word(map_address)

    if map_id == nil or map_id == 0 or map_id > HGSS_MAX_MAP_ID then
        local map_header = safe_read_dword(map_address)
        if is_main_ram_pointer(map_header) then
            local indirect_id = safe_read_word(map_header + 2)
            if indirect_id ~= nil and indirect_id <= HGSS_MAX_MAP_ID then
                map_id = indirect_id
            end
        end
    end

    if map_id == nil or map_id > HGSS_MAX_MAP_ID then
        return nil, "Aufenthaltsort wird ermittelt", field_base
    end

    return map_id, display_area(HGSS_AREA_BY_MAP[map_id], map_id), field_base
end

local live_emit_state = nil
local live_poll_callback = nil

for _, callback in ipairs(registered_callbacks) do
    local _, emit_state = find_upvalue(callback, "emit_live_state")
    if type(emit_state) == "function" then
        live_emit_state = emit_state
        live_poll_callback = callback
        break
    end
end

if live_emit_state ~= nil then
    local _, original_append_event = find_upvalue(live_emit_state, "append_event")
    if type(original_append_event) == "function" then
        write_upvalue(live_emit_state, "append_event", function(state)
            local map_id, location_name, field_base = read_current_location()
            state.locationId = map_id
            state.locationName = location_name

            state.diagnostics = state.diagnostics or {}
            state.diagnostics.liveMapId = map_id
            state.diagnostics.liveFieldBase = field_base
            state.diagnostics.locationSource = map_id ~= nil and "hgss-field-map" or "unresolved"

            return original_append_event(state)
        end)
    end
end

if live_poll_callback ~= nil and live_emit_state ~= nil then
    local last_live_map_id = nil
    local have_live_map_id = false

    write_upvalue(live_poll_callback, "emit_live_state", function(force)
        local map_id = select(1, read_current_location())
        local location_changed =
            (not have_live_map_id) or map_id ~= last_live_map_id

        last_live_map_id = map_id
        have_live_map_id = true

        return live_emit_state(force or location_changed)
    end)
end

local callback_is_running = false
local last_callback_frame = nil
local callback_driver_reported = false

local function current_frame_number()
    if emu == nil or type(emu.framecount) ~= "function" then
        return nil
    end

    local success, frame = pcall(emu.framecount)
    if not success or type(frame) ~= "number" then
        return nil
    end

    return frame
end

local function run_all_soulbuddy_callbacks(driver_name)
    if callback_is_running then
        return
    end

    local frame = current_frame_number()
    if frame ~= nil and last_callback_frame == frame then
        return
    end

    callback_is_running = true

    if not callback_driver_reported then
        print("[SoulBuddy] Frame-Callbacks aktiv über " .. tostring(driver_name) .. ".")
        callback_driver_reported = true
    end

    for index, callback in ipairs(registered_callbacks) do
        local success, callback_error = pcall(callback)
        if not success then
            print(
                "[SoulBuddy] Callback " .. tostring(index) ..
                " fehlgeschlagen: " .. tostring(callback_error))
        end
    end

    last_callback_frame = frame
    callback_is_running = false
end

native_gui_register(function()
    run_all_soulbuddy_callbacks("gui.register")
end)

if emu ~= nil and type(emu.registerafter) == "function" then
    local success, callback_error = pcall(emu.registerafter, function()
        run_all_soulbuddy_callbacks("emu.registerafter")
    end)

    if success then
        print("[SoulBuddy] emu.registerafter-Kompatibilitätsfallback registriert.")
    else
        print(
            "[SoulBuddy] emu.registerafter-Fallback konnte nicht registriert werden: " ..
            tostring(callback_error))
    end
else
    print("[SoulBuddy] emu.registerafter ist in dieser DeSmuME-Version nicht verfügbar.")
end

print("[SoulBuddy] Collector, Kampftracking und Overlay sind gemeinsam aktiv.")
print("[SoulBuddy] HGSS-Kampfstatus nutzt die dedizierte Battle-State-Adresse.")
print("[SoulBuddy] HGSS-Ortserkennung und Gegner-Leseweg sind aktiv.")
