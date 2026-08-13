return function(map)
    local function set(id, area) map[id] = area end
    local function range(first_id, last_id, area)
        for id = first_id, last_id do map[id] = area end
    end

    for id = 9, 26 do set(id, "route_" .. tostring(id - 8)) end

    local exact = {
        [6]="bell_tower", [7]="burned_tower", [8]="ruins_of_alph",
        [27]="route_22", [28]="route_24", [29]="route_25", [30]="route_26",
        [31]="route_27", [32]="route_28", [33]="route_29", [34]="route_30",
        [35]="route_31", [36]="route_32", [37]="route_33", [38]="route_34",
        [39]="route_35", [40]="route_36", [41]="route_37", [42]="route_38",
        [43]="route_39", [44]="route_42", [45]="route_43", [46]="route_44",
        [47]="route_45", [48]="route_46", [49]="pallet_town", [50]="viridian_city",
        [51]="pewter_city", [52]="cerulean_city", [53]="lavender_town",
        [54]="vermilion_city", [55]="celadon_city", [56]="fuchsia_city",
        [57]="cinnabar_island", [58]="indigo_plateau", [59]="saffron_city",
        [73]="violet_city", [74]="azalea_town", [75]="cianwood_city",
        [76]="goldenrod_city", [77]="olivine_city", [87]="mahogany_town",
        [88]="lake_of_rage", [89]="blackthorn_city", [90]="mt_silver",
        [91]="route_19", [92]="route_20", [93]="route_21", [94]="route_40",
        [95]="route_41", [96]="national_park", [97]="route_31", [98]="route_32",
        [99]="union_cave", [100]="azalea_town", [101]="route_35", [102]="route_35",
        [103]="route_36", [104]="route_36", [105]="ecruteak_city",
        [106]="digletts_cave", [107]="mt_moon", [108]="rock_tunnel",
        [109]="pal_park", [110]="sprout_tower", [111]="bell_tower",
        [112]="goldenrod_city", [113]="ruins_of_alph", [114]="slowpoke_well",
        [115]="olivine_lighthouse", [116]="team_rocket_hq", [117]="ilex_forest",
        [118]="goldenrod_city", [119]="mt_mortar", [120]="ice_path",
        [121]="whirl_islands", [122]="mt_silver_cave", [123]="dark_cave",
        [124]="victory_road", [125]="dragons_den", [126]="tohjo_falls",
        [127]="route_30", [132]="route_42", [133]="mahogany_town",
        [134]="route_29", [135]="violet_city", [136]="azalea_town",
        [137]="goldenrod_city", [138]="olivine_city", [139]="cianwood_city",
        [140]="mahogany_town", [141]="blackthorn_city", [142]="route_43",
        [143]="route_30", [144]="cherrygrove_city", [145]="cerulean_cave",
        [146]="seafoam_islands", [147]="viridian_forest", [148]="route_9",
        [149]="violet_city", [150]="national_park", [151]="route_47",
        [152]="route_48", [171]="route_34", [172]="route_38",
        [175]="ecruteak_city", [176]="dark_cave", [177]="slowpoke_well",
        [178]="victory_road", [179]="victory_road", [180]="azalea_town",
        [181]="slowpoke_well", [214]="route_39", [215]="route_39",
        [216]="ecruteak_city", [217]="burned_tower", [218]="ruins_of_alph",
        [219]="goldenrod_city", [240]="olivine_city", [241]="cianwood_city",
        [245]="route_43", [246]="mahogany_town", [253]="dragons_den"
    }

    for id, area in pairs(exact) do set(id, area) end

    range(60, 66, "new_bark_town")
    range(67, 72, "cherrygrove_city")
    range(78, 86, "ecruteak_city")
    range(128, 131, "ecruteak_city")
    range(153, 154, "union_cave")
    range(155, 156, "sprout_tower")
    range(157, 162, "violet_city")
    range(163, 166, "azalea_town")
    set(167, "violet_city")
    set(168, "azalea_town")
    range(169, 170, "route_32")
    range(173, 174, "safari_zone")
    range(182, 213, "goldenrod_city")
    range(220, 225, "olivine_lighthouse")
    range(226, 231, "olivine_city")
    range(232, 236, "cianwood_city")
    range(237, 239, "ice_path")
    range(242, 244, "whirl_islands")
    range(247, 249, "team_rocket_hq")
    range(250, 252, "mt_mortar")
end
