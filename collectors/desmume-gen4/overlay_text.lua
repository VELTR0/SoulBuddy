local M = {}

local function utf8_characters(value)
    local characters = {}
    local index = 1

    while index <= #value do
        local first_byte = string.byte(value, index) or 0
        local length = 1
        if first_byte >= 240 then
            length = 4
        elseif first_byte >= 224 then
            length = 3
        elseif first_byte >= 192 then
            length = 2
        end

        characters[#characters + 1] = string.sub(value, index, index + length - 1)
        index = index + length
    end

    return characters
end

local function utf8_length(value)
    return #utf8_characters(value)
end

local function split_long_word(word, maximum_characters)
    local characters = utf8_characters(word)
    local chunks = {}
    local index = 1

    while index <= #characters do
        local last_index = math.min(index + maximum_characters - 1, #characters)
        local chunk = {}
        for character_index = index, last_index do
            chunk[#chunk + 1] = characters[character_index]
        end
        chunks[#chunks + 1] = table.concat(chunk)
        index = last_index + 1
    end

    return chunks
end

function M.wrap(text, maximum_characters)
    local lines = {}
    local current_line = ""

    local function flush_current_line()
        if current_line ~= "" then
            lines[#lines + 1] = current_line
            current_line = ""
        end
    end

    for word in string.gmatch(text, "%S+") do
        local pieces = utf8_length(word) > maximum_characters
            and split_long_word(word, maximum_characters)
            or { word }

        for _, piece in ipairs(pieces) do
            if current_line == "" then
                current_line = piece
            else
                local candidate = current_line .. " " .. piece
                if utf8_length(candidate) <= maximum_characters then
                    current_line = candidate
                else
                    flush_current_line()
                    current_line = piece
                end
            end

            if utf8_length(current_line) >= maximum_characters then
                flush_current_line()
            end
        end
    end

    flush_current_line()
    if #lines == 0 then lines[1] = "" end
    return lines
end

return M
