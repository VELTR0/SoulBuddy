return function(map)
    local function set(id, area) map[id] = area end
    local function range(first_id, last_id, area)
        for id = first_id, last_id do map[id] = area end
    end

    local exact = {
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

    for id, area in pairs(exact) do set(id, area) end

    range(254, 278, "battle_frontier")
    range(280, 287, "national_park")
    range(289, 293, "blackthorn_city")
    range(299, 306, "indigo_plateau")
    range(307, 311, "ss_aqua")
    range(312, 321, "ruins_of_alph")
    range(323, 327, "ruins_of_alph")
    range(328, 329, "ss_aqua")
    range(332, 341, "bell_tower")
    range(343, 357, "safari_zone")
    range(358, 365, "vermilion_city")
    range(370, 383, "celadon_city")
    range(393, 395, "celadon_city")
    range(396, 397, "mahogany_town")
    range(398, 410, "saffron_city")
    range(412, 413, "goldenrod_city")
    range(426, 432, "cerulean_city")
    range(433, 439, "lavender_town")
    range(453, 458, "seafoam_islands")
    range(459, 465, "mt_silver_cave")
    range(471, 477, "pewter_city")
    range(478, 483, "fuchsia_city")
    set(485, "fuchsia_city")
    range(496, 502, "viridian_city")
    range(503, 507, "pallet_town")
    range(528, 531, "battle_frontier")
    range(534, 535, "safari_zone")
end
