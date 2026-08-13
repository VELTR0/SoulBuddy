-- Keeps the last readable incoming stream frame across very short file-replace gaps.
-- All unrelated io.open calls pass through unchanged.

local native_io_open = io.open
local cached_frame = nil
local missing_reads = 0
local missing_read_limit = 45

local function is_incoming_frame_path(path, mode)
    if type(path) ~= "string" or mode ~= "rb" then
        return false
    end

    local normalized = string.gsub(path, "\\", "/")
    local name = string.match(normalized, "([^/]+)$") or normalized
    if name == "stream-in.gd" then
        return true
    end

    return string.sub(name, 1, 10) == "stream-in." and
        string.sub(name, -3) == ".gd"
end

local function cached_file()
    return {
        read = function(_, format)
            if format == "*a" then
                return cached_frame
            end
            return nil
        end,
        close = function()
            return true
        end
    }
end

local function guarded_real_file(file)
    return {
        read = function(_, format)
            local data = file:read(format)
            if format == "*a" and type(data) == "string" and #data > 0 then
                cached_frame = data
                missing_reads = 0
            elseif format == "*a" and data == nil and cached_frame ~= nil and
                   missing_reads < missing_read_limit then
                missing_reads = missing_reads + 1
                return cached_frame
            end
            return data
        end,
        close = function()
            return file:close()
        end
    }
end

io.open = function(path, mode)
    if not is_incoming_frame_path(path, mode) then
        return native_io_open(path, mode)
    end

    local file, open_error = native_io_open(path, mode)
    if file ~= nil then
        return guarded_real_file(file)
    end

    missing_reads = missing_reads + 1
    if cached_frame ~= nil and missing_reads < missing_read_limit then
        return cached_file()
    end

    cached_frame = nil
    return nil, open_error
end

return true
